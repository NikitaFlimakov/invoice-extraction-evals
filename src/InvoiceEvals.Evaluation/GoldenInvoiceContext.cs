using System.Text.Json;

using InvoiceEvals.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>The golden <see cref="InvoiceDto"/> that every evaluator compares against.</summary>
public sealed class GoldenInvoiceContext(InvoiceDto expected)
    : EvaluationContext(ContextName, JsonSerializer.Serialize(expected, GoldenSet.JsonOptions))
{
    public const string ContextName = "Golden invoice";

    public InvoiceDto Expected { get; } = expected;
}

/// <summary>What every evaluator needs: the golden invoice and the parsed prediction (or why it did not parse).</summary>
internal sealed record EvaluationInputs(InvoiceDto Golden, InvoiceDto? Predicted, string? ParseError)
{
    public static EvaluationInputs From(ChatResponse response, IEnumerable<EvaluationContext>? additionalContext)
    {
        var golden = additionalContext?.OfType<GoldenInvoiceContext>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Evaluation requires a {nameof(GoldenInvoiceContext)}.");
        _ = InvoiceJson.TryParse(response.Text, out var predicted, out var error);
        return new EvaluationInputs(golden.Expected, predicted, error);
    }
}

internal static class Metrics
{
    /// <summary>A 0..1 metric that passes at or above <paramref name="passAt"/>; null means "not applicable" and is inconclusive.</summary>
    public static NumericMetric Score(string name, double? value, string reason, double passAt = 1.0)
    {
        var metric = new NumericMetric(name, value, reason);
        metric.Interpretation = value is not { } v
            ? new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, failed: false, "Not applicable to this document.")
            : new EvaluationMetricInterpretation(Rating(v), failed: v < passAt, v < passAt ? $"Below {passAt:0.##}." : null);
        return metric;
    }

    private static EvaluationRating Rating(double v) => v switch
    {
        >= 1.0 => EvaluationRating.Exceptional,
        >= 0.9 => EvaluationRating.Good,
        >= 0.7 => EvaluationRating.Average,
        >= 0.5 => EvaluationRating.Poor,
        _ => EvaluationRating.Unacceptable,
    };
}
