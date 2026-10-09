using System.Text.Json;
using System.Text.RegularExpressions;

using InvoiceEvals.Agent;
using InvoiceEvals.Core;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>
/// Deterministic metrics of the agent's tool use, read from the transcript in the response (function calls and their
/// results) and the answer's <c>warnings</c>. Agent configs only. Definitions in docs/metrics.md.
/// </summary>
public sealed partial class AgentBehaviorEvaluator : IEvaluator
{
    public const string ToolCallCount = "tool_call_count";
    public const string ValidateTotalsCalled = "validate_totals_called";
    /// <summary>normalize_currency called, on documents whose text layer shows a currency symbol but no ISO code.</summary>
    public const string NormalizeCurrencyCalledSymbol = "normalize_currency_called_symbol";
    /// <summary>normalize_currency called, on documents whose text layer shows an ISO code.</summary>
    public const string NormalizeCurrencyCalledCode = "normalize_currency_called_code";
    public const string ToolOverride = "tool_override";
    /// <summary>Per document with a totals_inconsistent warning: 1 if the golden amounts really do not reconcile.</summary>
    public const string TotalsWarningCorrect = "totals_warning_correct";

    public IReadOnlyCollection<string> EvaluationMetricNames =>
        [ToolCallCount, ValidateTotalsCalled, NormalizeCurrencyCalledSymbol, NormalizeCurrencyCalledCode, ToolOverride, TotalsWarningCorrect];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var (golden, predicted, _) = EvaluationInputs.From(modelResponse, additionalContext);
        var textLayer = additionalContext?.OfType<DocumentTextContext>().SingleOrDefault()?.TextLayer
            ?? throw new InvalidOperationException($"{nameof(AgentBehaviorEvaluator)} requires a {nameof(DocumentTextContext)}.");

        var contents = modelResponse.Messages.SelectMany(m => m.Contents).ToList();
        var calls = contents.OfType<FunctionCallContent>().ToList();
        var results = contents.OfType<FunctionResultContent>().ToDictionary(r => r.CallId, r => r.Result, StringComparer.Ordinal);
        bool Called(string tool) => calls.Any(c => c.Name == tool);

        var marker = CurrencyMarker(textLayer);
        var currencyCalled = Called(InvoiceTools.NormalizeCurrencyName) ? 1 : 0;
        var metrics = new List<EvaluationMetric>
        {
            new NumericMetric(ToolCallCount, calls.Count, string.Join(", ", calls.GroupBy(c => c.Name).Select(g => $"{g.Key} ×{g.Count()}"))),
            Metrics.Score(ValidateTotalsCalled, Called(InvoiceTools.ValidateTotalsName) ? 1 : 0, "validate_totals called at least once."),
            Metrics.Score(NormalizeCurrencyCalledSymbol, marker == Marker.Symbol ? currencyCalled : null, $"Text layer currency marker: {marker}."),
            Metrics.Score(NormalizeCurrencyCalledCode, marker == Marker.Code ? currencyCalled : null, $"Text layer currency marker: {marker}."),
        };

        // Reconciled totals that validate_totals reported as differing from the total it was given.
        var reconciled = calls.Where(c => c.Name == InvoiceTools.ValidateTotalsName)
            .Select(c => results.GetValueOrDefault(c.CallId))
            .Select(TotalsResult)
            .Where(r => r is { Consistent: false, ReconciledTotal: not null })
            .Select(r => r!.ReconciledTotal!.Value)
            .ToList();
        double? overridden = null;
        var overrideReason = "Not applicable: validate_totals never reported an inconsistency, the total is not printed, or the response did not parse.";
        if (predicted is not null && golden.Total is { } printed && reconciled.Count > 0)
        {
            var fixedIt = predicted.Total is { } p && !FieldAccuracyEvaluator.AmountsMatch(p, printed) && reconciled.Any(r => FieldAccuracyEvaluator.AmountsMatch(p, r));
            overridden = fixedIt ? 1 : 0;
            overrideReason = $"Predicted total {predicted.Total?.ToString() ?? "null"}; printed {printed}; validate_totals reconciled {string.Join(", ", reconciled)}.";
        }
        metrics.Add(new NumericMetric(ToolOverride, overridden, overrideReason)
        {
            Interpretation = overridden switch
            {
                null => new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, failed: false, "Not applicable to this document."),
                1 => new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, "Total replaced by the reconciled value."),
                _ => new EvaluationMetricInterpretation(EvaluationRating.Exceptional, failed: false),
            },
        });

        var totalsWarnings = AgentInvoiceExtractor.Warnings(ExtractionAnswer.Text(modelResponse))
            .Where(w => w.TrimStart().StartsWith(AgentInvoiceExtractor.TotalsWarning, StringComparison.OrdinalIgnoreCase)).ToList();
        var goldenInconsistent = golden is { Subtotal: { } s, Total: { } t } && !FieldAccuracyEvaluator.AmountsMatch(t, s - (golden.Discount ?? 0) + (golden.Tax ?? 0));
        metrics.Add(Metrics.Score(TotalsWarningCorrect, totalsWarnings.Count == 0 ? null : goldenInconsistent ? 1 : 0,
            totalsWarnings.Count == 0 ? "No totals warning." : $"Warning '{totalsWarnings[0]}'; golden amounts {(goldenInconsistent ? "do not reconcile" : "reconcile or are not all printed")}."));

        return ValueTask.FromResult(new EvaluationResult(metrics));
    }

    /// <summary>A validate_totals result as returned in process (<see cref="TotalsCheck"/>) or as JSON.</summary>
    private static TotalsCheck? TotalsResult(object? result)
    {
        if (result is null) return null;
        if (result is TotalsCheck check) return check;
        try
        {
            var json = result is JsonElement e ? e : JsonSerializer.SerializeToElement(result, AIJsonUtilities.DefaultOptions);
            return json.Deserialize<TotalsCheck>(AIJsonUtilities.DefaultOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal enum Marker
    {
        None,
        Symbol,
        Code,
    }

    /// <summary>Code when the text shows an ISO 4217 code of a currency in the eval set; else Symbol when it shows a currency sign; else None.</summary>
    internal static Marker CurrencyMarker(string text) =>
        IsoCode().IsMatch(text) ? Marker.Code : CurrencySign().IsMatch(text) ? Marker.Symbol : Marker.None;

    [GeneratedRegex(@"\b(USD|EUR|GBP|JPY|CHF|CAD|AUD|CNY|INR|SEK|NOK|DKK|PLN)\b")]
    private static partial Regex IsoCode();

    [GeneratedRegex(@"[$€£¥₹]")]
    private static partial Regex CurrencySign();
}
