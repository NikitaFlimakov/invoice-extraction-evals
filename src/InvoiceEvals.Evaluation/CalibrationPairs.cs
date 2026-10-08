using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceEvals.Evaluation;

/// <summary>One line of evals/judge/calibration_pairs.jsonl. <see cref="HumanLabel"/> is filled in by hand.</summary>
/// <param name="Id">Stable hash of field, golden and predicted; labels survive regeneration by id.</param>
/// <param name="Origin">"run:&lt;configs&gt;" for a real gray-zone mismatch, "synthetic:&lt;kind&gt;" for a perturbation.</param>
/// <param name="Excerpt">Text-layer excerpt around the annotated name: what the labeller and the judge both see.</param>
/// <param name="HumanLabel">true = same party, false = different, null = not labelled yet.</param>
public sealed record CalibrationPair(string Id, string Field, string DocId, string Golden, string Predicted, string Excerpt, string Origin, bool? HumanLabel);

/// <summary>
/// Builds the judge calibration set: every distinct real gray-zone mismatch, topped up to a target size with seeded
/// synthetic perturbations of golden names. Every pair is in the gray zone (strict match fails, both non-empty).
/// </summary>
public static class CalibrationPairs
{
    public static class Kind
    {
        public const string LegalSuffix = "legal_suffix";
        public const string Abbreviation = "abbreviation";
        public const string OcrNoise = "ocr_noise";
        public const string Reordered = "reordered";
        public const string SubstringTrap = "substring_trap";
        public const string DifferentParty = "different_party";
    }

    /// <summary>
    /// Synthetic slots cycle through this order. Real mismatches skew towards "same party, formatted differently",
    /// so the cycle leans the other way (3 likely-equivalent, 1 ambiguous, 4 likely-different) to land near half/half.
    /// </summary>
    public static IReadOnlyList<string> Cycle { get; } =
        [Kind.LegalSuffix, Kind.SubstringTrap, Kind.Abbreviation, Kind.DifferentParty, Kind.OcrNoise, Kind.SubstringTrap, Kind.Reordered, Kind.DifferentParty];

    public sealed record Source(string DocId, string Field, string Name, string TextLayer);

    public sealed record RealMismatch(string DocId, string Field, string Golden, string Predicted, string TextLayer, string Config);

    private const int MaxAttemptsPerSlot = 64;

    public static IReadOnlyList<CalibrationPair> Generate(IEnumerable<RealMismatch> real, IEnumerable<Source> sources, int target, ulong seed)
    {
        var pairs = new Dictionary<string, CalibrationPair>(StringComparer.Ordinal);
        foreach (var g in real
            .Where(m => IsGrayZone(m.Golden, m.Predicted))
            .GroupBy(m => Id(m.Field, m.Golden, m.Predicted), StringComparer.Ordinal))
        {
            var first = g.OrderBy(m => m.DocId, StringComparer.Ordinal).ThenBy(m => m.Config, StringComparer.Ordinal).First();
            var configs = string.Join(',', g.Select(m => m.Config).Distinct().Order(StringComparer.Ordinal));
            pairs[g.Key] = new CalibrationPair(g.Key, first.Field, first.DocId, first.Golden, first.Predicted,
                NameJudge.Excerpt(first.TextLayer, first.Golden, first.Predicted), $"run:{configs}", null);
        }

        var pool = sources
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .DistinctBy(s => (s.Field, FieldAccuracyEvaluator.NormalizeName(s.Name)))
            .OrderBy(s => s.Field, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.DocId, StringComparer.Ordinal)
            .ToList();
        var rng = new SplitMix64(seed);
        for (var slot = 0; pairs.Count < target && pool.Count > 1 && slot < target * Cycle.Count; slot++)
        {
            var kind = Cycle[slot % Cycle.Count];
            for (var attempt = 0; attempt < MaxAttemptsPerSlot; attempt++)
            {
                var source = pool[rng.NextInt(pool.Count)];
                var predicted = Perturb(kind, source, pool, ref rng);
                if (predicted is null || !IsGrayZone(source.Name, predicted)) continue;
                var id = Id(source.Field, source.Name, predicted);
                if (pairs.ContainsKey(id)) continue;
                pairs[id] = new CalibrationPair(id, source.Field, source.DocId, source.Name, predicted,
                    NameJudge.Excerpt(source.TextLayer, source.Name, predicted), $"synthetic:{kind}", null);
                break;
            }
        }
        return [.. pairs.Values.OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>Carries <see cref="CalibrationPair.HumanLabel"/> over from <paramref name="existing"/> by id.</summary>
    public static IReadOnlyList<CalibrationPair> KeepLabels(IReadOnlyList<CalibrationPair> generated, IEnumerable<CalibrationPair> existing)
    {
        var labels = existing.Where(p => p.HumanLabel is not null).ToDictionary(p => p.Id, p => p.HumanLabel, StringComparer.Ordinal);
        return [.. generated.Select(p => labels.TryGetValue(p.Id, out var label) ? p with { HumanLabel = label } : p)];
    }

    public static bool IsGrayZone(string? golden, string? predicted) =>
        !string.IsNullOrWhiteSpace(golden) && !string.IsNullOrWhiteSpace(predicted) && !FieldAccuracyEvaluator.NamesMatch(golden, predicted);

    public static string Id(string field, string golden, string predicted) =>
        "p-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{field}\n{golden}\n{predicted}")))[..12];

    // ---------- perturbations ----------

    // Suffixes the strict normalizer does not strip, so adding one lands in the gray zone.
    private static readonly string[] UnlistedSuffixes = [" Pvt. Ltd.", ", S.A. de C.V.", " Sp. z o.o.", " A/S", " KGaA", " Ltda.", " S.p.A. Unipersonale"];

    private static readonly (string Full, string Short)[] Abbreviations =
    [
        ("International", "Intl."), ("Manufacturing", "Mfg."), ("Brothers", "Bros."), ("Services", "Svcs."),
        ("Technologies", "Tech."), ("Technology", "Tech."), ("Associates", "Assoc."), ("Industries", "Inds."),
        ("Solutions", "Sol."), ("Systems", "Sys."), ("Group", "Grp."), ("Laboratories", "Labs"),
        ("Engineering", "Eng."), ("Mountain", "Mtn."), ("Saint", "St."), ("Department", "Dept."),
        ("Distribution", "Dist."), ("Management", "Mgmt."), ("National", "Natl."), ("Products", "Prods."),
    ];

    private static readonly string[] TrapAdditions = ["Logistics", "Holdings", "Capital Partners", "Medical Supplies", "Realty Group", "Foods"];

    private static readonly string[] TrapPrefixes = ["North", "New", "United"];

    private static readonly Dictionary<char, string> Confusables = new()
    {
        ['O'] = "0", ['o'] = "0", ['0'] = "O", ['l'] = "1", ['1'] = "l", ['I'] = "l", ['i'] = "l", ['S'] = "5", ['s'] = "5",
        ['B'] = "8", ['e'] = "c", ['c'] = "e", ['m'] = "rn", ['n'] = "ri", ['a'] = "o", ['g'] = "q", ['h'] = "b", ['u'] = "v",
        ['E'] = "F", ['G'] = "C", ['D'] = "O", ['t'] = "f",
    };

    private static string? Perturb(string kind, Source source, List<Source> pool, ref SplitMix64 rng)
    {
        var name = source.Name.Trim();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (kind)
        {
            case Kind.LegalSuffix:
                return name.TrimEnd('.', ',') + UnlistedSuffixes[rng.NextInt(UnlistedSuffixes.Length)];

            case Kind.Abbreviation:
                for (var i = 0; i < words.Length; i++)
                {
                    foreach (var (full, abbr) in Abbreviations)
                    {
                        if (string.Equals(words[i].TrimEnd(',', '.'), full, StringComparison.OrdinalIgnoreCase)) return Replace(words, i, abbr);
                        if (string.Equals(words[i].TrimEnd(','), abbr, StringComparison.OrdinalIgnoreCase)) return Replace(words, i, full);
                    }
                }
                return words.Length >= 2 && char.IsLetter(words[0][0]) ? Replace(words, 0, $"{char.ToUpperInvariant(words[0][0])}.") : null;

            case Kind.OcrNoise:
            {
                var chars = new StringBuilder(name);
                var edits = 1 + rng.NextInt(3);
                for (var e = 0; e < edits; e++)
                {
                    for (var attempt = 0; attempt < 8; attempt++)
                    {
                        var at = rng.NextInt(chars.Length);
                        if (!Confusables.TryGetValue(chars[at], out var replacement)) continue;
                        chars.Remove(at, 1).Insert(at, replacement);
                        break;
                    }
                }
                return chars.ToString();
            }

            case Kind.Reordered:
                if (words.Length < 2) return null;
                return words.Length == 2 ? $"{words[1].TrimEnd(',')}, {words[0].TrimEnd(',')}" : string.Join(' ', [words[^1].TrimEnd(','), .. words[..^1]]);

            case Kind.SubstringTrap:
                return rng.NextInt(3) == 0
                    ? $"{TrapPrefixes[rng.NextInt(TrapPrefixes.Length)]} {name}"
                    : $"{name.TrimEnd('.', ',')} {TrapAdditions[rng.NextInt(TrapAdditions.Length)]}";

            case Kind.DifferentParty:
            {
                var other = pool[rng.NextInt(pool.Count)];
                return FieldAccuracyEvaluator.NamesMatch(other.Name, name) ? null : other.Name.Trim();
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown perturbation kind.");
        }
    }

    private static string Replace(string[] words, int index, string replacement)
    {
        var copy = (string[])words.Clone();
        copy[index] = words[index].EndsWith(',') ? replacement + "," : replacement;
        return string.Join(' ', copy);
    }

    // ---------- JSON Lines I/O ----------

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static async Task<IReadOnlyList<CalibrationPair>> ReadAsync(string path, CancellationToken ct)
    {
        var pairs = new List<CalibrationPair>();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                pairs.Add(JsonSerializer.Deserialize<CalibrationPair>(line, JsonOptions) ?? throw new InvalidDataException("null record"));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: {ex.Message}", ex);
            }
        }
        return pairs;
    }

    public static Task WriteAsync(string path, IEnumerable<CalibrationPair> pairs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs) sb.Append(JsonSerializer.Serialize(p, JsonOptions)).Append('\n');
        return File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }
}
