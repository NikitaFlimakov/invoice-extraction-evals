using System.Globalization;
using System.Text;

using InvoiceEvals.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>
/// One 0/1 metric per scored field. Golden null means "not printed": predicted null is correct, a value is a false
/// positive. Each metric carries <c>Metadata["outcome"]</c> so reports can compute false-positive and miss rates.
/// Comparison rules are in docs/metrics.md.
/// </summary>
/// <param name="vendorNameMatch">
/// Extension point for the Phase 3 vendor-name judge: (golden, predicted) → match. Defaults to <see cref="NamesMatch"/>.
/// </param>
public sealed class FieldAccuracyEvaluator(Func<string, string, bool>? vendorNameMatch = null) : IEvaluator
{
    public const string OutcomeKey = "outcome";

    public static class Outcome
    {
        public const string CorrectValue = "correct_value";
        public const string CorrectNull = "correct_null";
        public const string Mismatch = "mismatch";
        public const string Miss = "miss";
        public const string FalsePositive = "false_positive";
        public const string Unparsed = "unparsed";
    }

    private static (string Name, Func<InvoiceDto, object?> Get, Func<object, object, bool> Equal)[] Fields(Func<string, string, bool> vendorNameMatch) =>
    [
        ("invoice_number", d => d.InvoiceNumber, (a, b) => Fold((string)a) == Fold((string)b)),
        ("invoice_date", d => d.InvoiceDate, (a, b) => (DateOnly)a == (DateOnly)b),
        ("due_date", d => d.DueDate, (a, b) => (DateOnly)a == (DateOnly)b),
        ("currency", d => d.Currency, (a, b) => Fold((string)a) == Fold((string)b)),
        ("subtotal", d => d.Subtotal, AmountsMatch),
        ("discount", d => d.Discount, AmountsMatch),
        ("tax", d => d.Tax, AmountsMatch),
        ("total", d => d.Total, AmountsMatch),
        ("vendor_name", d => d.Vendor?.Name, (a, b) => vendorNameMatch((string)a, (string)b)),
        ("customer_name", d => d.Customer?.Name, (a, b) => NamesMatch((string)a, (string)b)),
    ];

    public static IReadOnlyList<string> FieldNames { get; } = [.. Fields(NamesMatch).Select(f => f.Name)];

    public IReadOnlyCollection<string> EvaluationMetricNames => [.. FieldNames.Select(MetricName)];

    public static string MetricName(string field) => $"field.{field}";

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var (golden, predicted, _) = EvaluationInputs.From(modelResponse, additionalContext);
        var metrics = Fields(vendorNameMatch ?? NamesMatch).Select(f =>
        {
            var expected = f.Get(golden);
            var actual = predicted is null ? null : f.Get(predicted);
            var outcome = (predicted, expected, actual) switch
            {
                (null, _, _) => Outcome.Unparsed,
                (_, null, null) => Outcome.CorrectNull,
                (_, null, _) => Outcome.FalsePositive,
                (_, _, null) => Outcome.Miss,
                _ => f.Equal(expected!, actual!) ? Outcome.CorrectValue : Outcome.Mismatch,
            };
            var correct = outcome is Outcome.CorrectValue or Outcome.CorrectNull;
            var metric = Metrics.Score(MetricName(f.Name), correct ? 1 : 0, $"{outcome}: expected {Show(expected)}, got {Show(actual)}");
            metric.Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [OutcomeKey] = outcome };
            return metric;
        });
        return ValueTask.FromResult(new EvaluationResult(metrics));
    }

    /// <summary>Amounts match within 0.01 inclusive.</summary>
    public static bool AmountsMatch(object a, object b) => Math.Abs((decimal)a - (decimal)b) <= 0.01m;

    /// <summary>Names match after <see cref="NormalizeName"/>.</summary>
    public static bool NamesMatch(string a, string b) => NormalizeName(a) == NormalizeName(b);

    // Legal-form suffixes stripped from the end of a name, as normalized token sequences. Longest first.
    private static readonly string[][] LegalSuffixes =
    [
        .. new[]
        {
            "gmbh and co kg", "s a r l", "s à r l", "s p a", "s r l", "s a s", "pty ltd", "co ltd", "and co kg", "and co",
            "s a", "b v", "n v", "k k", "s l", "co kg",
            "inc", "incorporated", "llc", "llp", "lp", "ltd", "limited", "plc", "corp", "corporation", "co", "company",
            "gmbh", "ag", "kg", "ug", "sa", "sas", "sarl", "srl", "spa", "bv", "nv", "kk", "oy", "oyj", "ab", "as", "aps", "pty", "sl", "se",
        }.Select(s => s.Split(' ')).OrderByDescending(s => s.Length),
    ];

    /// <summary>Lower-case, strip punctuation, "&amp;" → "and", then drop trailing legal-form suffixes. Diacritics are kept.</summary>
    public static string NormalizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Replace("&", " and ", StringComparison.Ordinal))
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        for (var stripped = true; stripped;)
        {
            stripped = false;
            foreach (var suffix in LegalSuffixes)
            {
                if (tokens.Count > suffix.Length && tokens[^suffix.Length..].SequenceEqual(suffix, StringComparer.Ordinal))
                {
                    tokens.RemoveRange(tokens.Count - suffix.Length, suffix.Length);
                    stripped = true;
                    break;
                }
            }
        }
        return string.Join(' ', tokens);
    }

    private static string Fold(string s) => s.Trim().ToUpperInvariant();

    private static string Show(object? v) => v switch
    {
        null => "null",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        _ => $"'{v}'",
    };
}
