using System.Globalization;
using System.Text;
using System.Text.Json;

using InvoiceEvals.Core;

namespace InvoiceEvals.Cli;

/// <summary>One gated metric: a summary.csv column on one subset, with the largest absolute drop allowed.</summary>
internal sealed record Threshold(string Metric, string Subset, double MaxDrop);

/// <summary>(config, subset, metric) → value; null value = "n/a" in the summary.</summary>
internal sealed class MetricTable : Dictionary<(string Config, string Subset, string Metric), double?>
{
    public IEnumerable<string> Configs => Keys.Select(k => k.Config).Distinct(StringComparer.Ordinal);
}

internal sealed record GateRow(string Config, string Metric, string Subset, double? Baseline, double? Current, double MaxDrop, string Result, bool Breach);

/// <summary>Regression gate: compares the latest summary against the committed baseline with per-metric absolute max drops.</summary>
internal static class Gate
{
    // Values are written with four decimals; anything smaller than this is formatting noise.
    private const double Epsilon = 1e-9;

    public static async Task<IReadOnlyList<Threshold>> LoadThresholdsAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer.DeserializeAsync<ThresholdsFile>(stream, GoldenSet.JsonOptions, ct)
            ?? throw new InvalidDataException($"{path}: empty.");
        if (file.Thresholds is not { Count: > 0 }) throw new InvalidDataException($"{path}: no thresholds.");
        foreach (var t in file.Thresholds)
        {
            if (string.IsNullOrWhiteSpace(t.Metric) || string.IsNullOrWhiteSpace(t.Subset) || t.MaxDrop < 0)
                throw new InvalidDataException($"{path}: invalid threshold {t}. Need metric, subset and maxDrop ≥ 0.");
        }
        return file.Thresholds;
    }

    private sealed record ThresholdsFile(List<Threshold> Thresholds);

    /// <summary>The latest execution per configuration from summary.csv, as a metric table.</summary>
    public static MetricTable ReadLatestSummary(string path, IReadOnlyList<Threshold> thresholds)
    {
        var rows = Csv.Read(path);
        if (rows.Count == 0) throw new InvalidDataException($"{path}: empty.");
        var header = rows[0];
        int Column(string name) => Array.IndexOf(header, name) is var i and >= 0 ? i : throw new InvalidDataException($"{path}: no column '{name}'.");
        var (execution, config, subset) = (Column("execution"), Column("config"), Column("subset"));
        var metricColumns = thresholds.Select(t => t.Metric).Distinct(StringComparer.Ordinal)
            .Select(m => (Name: m, Index: Array.IndexOf(header, m))).ToList();
        if (metricColumns.FirstOrDefault(m => m.Index < 0) is { Name: { } unknown })
            throw new InvalidDataException($"evals/thresholds.json gates '{unknown}', which is not a column of {path}.");

        var latest = rows.Skip(1).GroupBy(r => r[config], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(r => r[execution]).Max(StringComparer.Ordinal)!, StringComparer.Ordinal);
        var table = new MetricTable();
        foreach (var r in rows.Skip(1).Where(r => r[execution] == latest[r[config]]))
        {
            foreach (var (name, index) in metricColumns) table[(r[config], r[subset], name)] = Parse(r[index]);
        }
        return table;
    }

    public static MetricTable ReadBaseline(string path)
    {
        var rows = Csv.Read(path);
        if (rows.Count == 0 || !rows[0].SequenceEqual(BaselineHeader, StringComparer.Ordinal))
            throw new InvalidDataException($"{path}: expected header {string.Join(',', BaselineHeader)}.");
        var table = new MetricTable();
        foreach (var r in rows.Skip(1)) table[(r[0], r[1], r[2])] = Parse(r[3]);
        return table;
    }

    private static readonly string[] BaselineHeader = ["config", "subset", "metric", "value"];

    /// <summary>baseline.csv: only the gated (config, subset, metric) cells, ordered, LF endings.</summary>
    public static string BaselineCsv(MetricTable current, IReadOnlyList<Threshold> thresholds)
    {
        var sb = new StringBuilder(string.Join(',', BaselineHeader)).Append('\n');
        foreach (var config in OrderConfigs(current.Configs))
        {
            foreach (var t in thresholds)
            {
                if (current.TryGetValue((config, t.Subset, t.Metric), out var value))
                    sb.Append(CultureInfo.InvariantCulture, $"{config},{t.Subset},{t.Metric},{Format(value)}\n");
            }
        }
        return sb.ToString();
    }

    public static IReadOnlyList<GateRow> Evaluate(MetricTable baseline, MetricTable current, IReadOnlyList<Threshold> thresholds)
    {
        var baselineConfigs = baseline.Configs.ToHashSet(StringComparer.Ordinal);
        var currentConfigs = current.Configs.ToHashSet(StringComparer.Ordinal);
        var rows = new List<GateRow>();
        foreach (var config in OrderConfigs(baselineConfigs.Union(currentConfigs, StringComparer.Ordinal)))
        {
            foreach (var t in thresholds)
            {
                var key = (config, t.Subset, t.Metric);
                var hasBaseline = baseline.TryGetValue(key, out var b);
                var hasCurrent = current.TryGetValue(key, out var c);
                var (result, breach) = (hasBaseline, b, hasCurrent, c) switch
                {
                    (false, _, _, _) when !baselineConfigs.Contains(config) => ("new config, no baseline", false),
                    (false, _, _, _) => ("no baseline", false),
                    (true, _, false, _) when !currentConfigs.Contains(config) => ("❌ config not run", true),
                    (true, null, _, _) => ("n/a", false),
                    (true, not null, _, null) => ("❌ missing", true),
                    _ when b!.Value - c!.Value > t.MaxDrop + Epsilon => ("❌ regression", true),
                    _ => ("✅", false),
                };
                if (!hasBaseline && !hasCurrent) continue;
                rows.Add(new GateRow(config, t.Metric, t.Subset, b, c, t.MaxDrop, result, breach));
            }
        }
        return rows;
    }

    public static string Markdown(IReadOnlyList<GateRow> rows, string baselinePath, string thresholdsPath)
    {
        var breaches = rows.Count(r => r.Breach);
        var sb = new StringBuilder();
        sb.Append(breaches == 0 ? "### ✅ Eval gate passed\n\n" : $"### ❌ Eval gate failed: {breaches} breach{(breaches == 1 ? "" : "es")}\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Current run vs `{baselinePath}`; max drops (absolute) from `{thresholdsPath}`.\n\n");
        sb.Append("| Config | Metric | Subset | Baseline | Current | Δ | Max drop | Result |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var r in rows)
        {
            var delta = r is { Baseline: { } b, Current: { } c } ? (c - b).ToString("+0.0000;-0.0000;0.0000", CultureInfo.InvariantCulture) : "";
            sb.Append(CultureInfo.InvariantCulture,
                $"| `{r.Config}` | {r.Metric} | {r.Subset} | {Format(r.Baseline, "n/a")} | {Format(r.Current, "n/a")} | {delta} | {r.MaxDrop:0.0000} | {r.Result} |\n");
        }
        return sb.ToString();
    }

    private static IEnumerable<string> OrderConfigs(IEnumerable<string> configs) =>
        configs.OrderBy(ReportCommand.ConfigOrder).ThenBy(c => c, StringComparer.Ordinal);

    private static double? Parse(string cell) =>
        cell.Length == 0 ? null : double.Parse(cell, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Format(double? value, string empty = "") => value?.ToString("0.0000", CultureInfo.InvariantCulture) ?? empty;
}

/// <summary>Minimal RFC 4180 reader for the CSVs `evals report` writes (quoted cells, doubled quotes).</summary>
internal static class Csv
{
    public static List<string[]> Read(string path) => Parse(File.ReadAllText(path));

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch != '"') cell.Append(ch);
                else if (i + 1 < text.Length && text[i + 1] == '"') cell.Append(text[++i]);
                else quoted = false;
                continue;
            }
            switch (ch)
            {
                case '"': quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n': row.Add(cell.ToString()); cell.Clear(); rows.Add([.. row]); row.Clear(); break;
                default: cell.Append(ch); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add([.. row]);
        }
        if (quoted) throw new InvalidDataException("Unterminated quoted CSV cell.");
        return rows;
    }
}
