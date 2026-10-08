using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>
/// 1 if the response parses into InvoiceDto (dates as yyyy-MM-dd), every line item has a description, at least one of
/// invoiceNumber / invoiceDate / total is non-null, and currency is a 3-letter code when present. Else 0, with each
/// violation recorded as an error diagnostic. See docs/metrics.md.
/// </summary>
public sealed partial class SchemaValidityEvaluator : IEvaluator
{
    public const string MetricName = "schema_validity";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var (_, predicted, parseError) = EvaluationInputs.From(modelResponse, additionalContext);

        var violations = new List<string>();
        if (predicted is null)
        {
            violations.Add($"Does not parse into InvoiceDto: {parseError}");
        }
        else
        {
            if (predicted.InvoiceNumber is null && predicted.InvoiceDate is null && predicted.Total is null)
                violations.Add("invoiceNumber, invoiceDate and total are all null.");
            if (predicted.Currency is { } currency && !Iso4217Shape().IsMatch(currency))
                violations.Add($"currency '{currency}' is not a 3-letter code.");
            var missing = predicted.LineItems?.Select((l, i) => (l, i)).Where(x => string.IsNullOrWhiteSpace(x.l.Description)).Select(x => x.i).ToList();
            if (missing is { Count: > 0 })
                violations.Add($"lineItems[{string.Join(", ", missing)}] have no description.");
        }

        var metric = Metrics.Score(MetricName, violations.Count == 0 ? 1 : 0,
            violations.Count == 0 ? "Valid." : $"{violations.Count} violation(s).");
        metric.AddDiagnostics(violations.Select(EvaluationDiagnostic.Error));
        return ValueTask.FromResult(new EvaluationResult(metric));
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Iso4217Shape();
}
