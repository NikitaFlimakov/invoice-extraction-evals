using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>
/// Detects a model "fixing" arithmetic: 1 when the predicted total differs from the printed total but equals
/// printed subtotal − discount + tax (all within 0.01), else 0. Not applicable (null) when the golden total or subtotal
/// is not printed, when the printed amounts already reconcile (the two cases are indistinguishable), or when the
/// response does not parse.
/// </summary>
public sealed class RecomputedTotalEvaluator : IEvaluator
{
    public const string MetricName = "recomputed_total_rate";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var (golden, predicted, _) = EvaluationInputs.From(modelResponse, additionalContext);

        double? value = null;
        var reason = "Not applicable.";
        if (predicted is not null && golden is { Total: { } printed, Subtotal: { } subtotal })
        {
            var recomputed = subtotal - (golden.Discount ?? 0) + (golden.Tax ?? 0);
            if (!FieldAccuracyEvaluator.AmountsMatch(printed, recomputed))
            {
                var fixedIt = predicted.Total is { } p && !FieldAccuracyEvaluator.AmountsMatch(p, printed) && FieldAccuracyEvaluator.AmountsMatch(p, recomputed);
                value = fixedIt ? 1 : 0;
                reason = fixedIt
                    ? $"Predicted {predicted.Total} equals recomputed {recomputed}, not printed {printed}."
                    : $"Predicted {predicted.Total?.ToString() ?? "null"}; printed {printed}, recomputed {recomputed}.";
            }
        }

        var metric = new NumericMetric(MetricName, value, reason)
        {
            Interpretation = value is null
                ? new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, failed: false, "Not applicable to this document.")
                : value == 1
                    ? new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, "Total was recomputed instead of extracted.")
                    : new EvaluationMetricInterpretation(EvaluationRating.Exceptional, failed: false),
        };
        return ValueTask.FromResult(new EvaluationResult(metric));
    }
}
