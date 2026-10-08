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
/// <param name="judge">
/// Judge-assisted mode: also emits <c>field.vendor_name_judged</c> and <c>field.customer_name_judged</c>, which equal
/// the strict metric except in the gray zone (strict mismatch, both names non-null), where <paramref name="judge"/>
/// decides. Requires a <see cref="DocumentTextContext"/> and a chat configuration. Strict metrics are unchanged.
/// </param>
public sealed class FieldAccuracyEvaluator(NameJudge? judge = null) : IEvaluator
{
    public const string OutcomeKey = "outcome";

    /// <summary>Metadata key on judged metrics: how the judged score was reached, one of <see cref="JudgeOutcome"/>.</summary>
    public const string JudgeKey = "judge";

    public static class JudgeOutcome
    {
        /// <summary>Not in the gray zone; the judged score is the strict score.</summary>
        public const string NotNeeded = "not_needed";
        public const string Equivalent = "equivalent";
        public const string NotEquivalent = "not_equivalent";
        /// <summary>The judge response did not parse; scored as a mismatch.</summary>
        public const string Unparsed = "unparsed";
        /// <summary>The vendor name is not in the model's input (FATURA ocr mode); the judge is not asked, scored as a mismatch.</summary>
        public const string NotInModelInput = "not_in_model_input";
    }

    public static class Outcome
    {
        public const string CorrectValue = "correct_value";
        public const string CorrectNull = "correct_null";
        public const string Mismatch = "mismatch";
        public const string Miss = "miss";
        public const string FalsePositive = "false_positive";
        public const string Unparsed = "unparsed";
    }

    private static readonly (string Name, Func<InvoiceDto, object?> Get, Func<object, object, bool> Equal)[] Fields =
    [
        ("invoice_number", d => d.InvoiceNumber, (a, b) => Fold((string)a) == Fold((string)b)),
        ("invoice_date", d => d.InvoiceDate, (a, b) => (DateOnly)a == (DateOnly)b),
        ("due_date", d => d.DueDate, (a, b) => (DateOnly)a == (DateOnly)b),
        ("currency", d => d.Currency, (a, b) => Fold((string)a) == Fold((string)b)),
        ("subtotal", d => d.Subtotal, AmountsMatch),
        ("discount", d => d.Discount, AmountsMatch),
        ("tax", d => d.Tax, AmountsMatch),
        ("total", d => d.Total, AmountsMatch),
        ("vendor_name", d => d.Vendor?.Name, (a, b) => NamesMatch((string)a, (string)b)),
        ("customer_name", d => d.Customer?.Name, (a, b) => NamesMatch((string)a, (string)b)),
    ];

    public static IReadOnlyList<string> FieldNames { get; } = [.. Fields.Select(f => f.Name)];

    /// <summary>Fields that get a judged metric in judge-assisted mode.</summary>
    public static IReadOnlyList<string> JudgedFieldNames { get; } = ["vendor_name", "customer_name"];

    public IReadOnlyCollection<string> EvaluationMetricNames =>
        judge is null ? [.. FieldNames.Select(MetricName)] : [.. FieldNames.Select(MetricName), .. JudgedFieldNames.Select(JudgedMetricName)];

    public static string MetricName(string field) => $"field.{field}";

    public static string JudgedMetricName(string field) => $"field.{field}_judged";

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var (golden, predicted, _) = EvaluationInputs.From(modelResponse, additionalContext);
        var metrics = new List<EvaluationMetric>(Fields.Length + JudgedFieldNames.Count);
        var strict = new Dictionary<string, (string Outcome, object? Expected, object? Actual)>(StringComparer.Ordinal);
        foreach (var f in Fields)
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
            strict[f.Name] = (outcome, expected, actual);
            metrics.Add(FieldMetric(MetricName(f.Name), outcome, $"{outcome}: expected {Show(expected)}, got {Show(actual)}"));
        }

        if (judge is not null)
        {
            var text = additionalContext?.OfType<DocumentTextContext>().SingleOrDefault()
                ?? throw new InvalidOperationException($"Judge-assisted evaluation requires a {nameof(DocumentTextContext)}.");
            var chat = chatConfiguration?.ChatClient
                ?? throw new InvalidOperationException("Judge-assisted evaluation requires a chat configuration.");
            metrics.AddRange(await Task.WhenAll(JudgedFieldNames.Select(f => JudgedAsync(judge, chat, f, strict[f], text, cancellationToken))));
        }
        return new EvaluationResult(metrics);
    }

    private static async Task<EvaluationMetric> JudgedAsync(
        NameJudge judge, IChatClient chat, string field, (string Outcome, object? Expected, object? Actual) strict, DocumentTextContext text, CancellationToken ct)
    {
        var name = JudgedMetricName(field);
        var shown = $"expected {Show(strict.Expected)}, got {Show(strict.Actual)}";
        if (strict.Outcome != Outcome.Mismatch)
            return FieldMetric(name, strict.Outcome, $"{strict.Outcome}: {shown}", JudgeOutcome.NotNeeded);
        if (field == "vendor_name" && !text.VendorNameInModelInput)
            return FieldMetric(name, Outcome.Mismatch, $"{Outcome.Mismatch}: {shown}; vendor name is not in the model input, judge not asked", JudgeOutcome.NotInModelInput);

        var verdict = await judge.JudgeAsync(chat, field, (string)strict.Expected!, (string)strict.Actual!, text.TextLayer, ct);
        var (outcome, how) = verdict.Equivalent switch
        {
            true => (Outcome.CorrectValue, JudgeOutcome.Equivalent),
            false => (Outcome.Mismatch, JudgeOutcome.NotEquivalent),
            null => (Outcome.Mismatch, JudgeOutcome.Unparsed),
        };
        var metric = FieldMetric(name, outcome, $"{how}: {shown}. Judge: {verdict.Reason}", how);
        if (how == JudgeOutcome.Unparsed) metric.AddDiagnostics(EvaluationDiagnostic.Warning($"Judge response did not parse ({verdict.Reason}); scored as mismatch."));
        return metric;
    }

    private static NumericMetric FieldMetric(string name, string outcome, string reason, string? judgeOutcome = null)
    {
        var metric = Metrics.Score(name, outcome is Outcome.CorrectValue or Outcome.CorrectNull ? 1 : 0, reason);
        metric.Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { [OutcomeKey] = outcome };
        if (judgeOutcome is not null) metric.Metadata[JudgeKey] = judgeOutcome;
        return metric;
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
