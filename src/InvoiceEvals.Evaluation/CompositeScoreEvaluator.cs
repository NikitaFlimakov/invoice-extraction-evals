using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>
/// Runs the four deterministic evaluators and adds <c>composite</c>, their weighted mean. A component that is not
/// applicable to a document (line items on FATURA, recomputed total when amounts reconcile) is dropped and the
/// remaining weights are rescaled to sum to 1. Weights are documented in docs/metrics.md.
/// </summary>
public sealed class CompositeScoreEvaluator(Func<string, string, bool>? vendorNameMatch = null) : IEvaluator
{
    public const string MetricName = "composite";

    /// <summary>Schema validity, mean field accuracy, line-items F1, and 1 − recomputed_total_rate.</summary>
    public static readonly (double Schema, double Fields, double LineItems, double NotRecomputed) Weights = (0.1, 0.6, 0.2, 0.1);

    private readonly IEvaluator[] parts =
    [
        new SchemaValidityEvaluator(),
        new FieldAccuracyEvaluator(vendorNameMatch),
        new RecomputedTotalEvaluator(),
        new LineItemsF1Evaluator(),
    ];

    public IReadOnlyCollection<string> EvaluationMetricNames => [.. parts.SelectMany(p => p.EvaluationMetricNames), MetricName];

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var metrics = new List<EvaluationMetric>();
        foreach (var part in parts)
            metrics.AddRange((await part.EvaluateAsync(messages, modelResponse, chatConfiguration, additionalContext, cancellationToken)).Metrics.Values);
        var values = metrics.OfType<NumericMetric>().ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal);

        var fieldMean = FieldAccuracyEvaluator.FieldNames.Average(f => values[FieldAccuracyEvaluator.MetricName(f)]!.Value);
        (double Weight, double? Value)[] components =
        [
            (Weights.Schema, values[SchemaValidityEvaluator.MetricName]),
            (Weights.Fields, fieldMean),
            (Weights.LineItems, values[LineItemsF1Evaluator.F1]),
            (Weights.NotRecomputed, 1 - values[RecomputedTotalEvaluator.MetricName]),
        ];
        var applicable = components.Where(c => c.Value is not null).ToList();
        var score = applicable.Sum(c => c.Weight * c.Value!.Value) / applicable.Sum(c => c.Weight);

        metrics.Add(Metrics.Score(MetricName, score, $"Weighted mean of {applicable.Count} applicable components.", passAt: 0.9));
        return new EvaluationResult(metrics);
    }
}
