using InvoiceEvals.Core;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>
/// Precision, recall and F1 of predicted line items. Golden and predicted lines are matched greedily (golden order,
/// best description overlap first) when amounts agree within 0.01 and description token Jaccard ≥ 0.5.
/// Not applicable (null) when golden lineItems is null (FATURA: not annotated). Rules in docs/metrics.md.
/// </summary>
public sealed class LineItemsF1Evaluator : IEvaluator
{
    public const string Precision = "line_items_precision";
    public const string Recall = "line_items_recall";
    public const string F1 = "line_items_f1";
    public const double MinOverlap = 0.5;

    public IReadOnlyCollection<string> EvaluationMetricNames => [Precision, Recall, F1];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages, ChatResponse modelResponse, ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        var (golden, predicted, _) = EvaluationInputs.From(modelResponse, additionalContext);
        if (golden.LineItems is null)
        {
            const string NotAnnotated = "Golden line items are not annotated.";
            return Result(null, null, null, NotAnnotated);
        }

        var gold = golden.LineItems;
        var pred = predicted?.LineItems ?? [];
        var matched = Match(gold, pred);

        // Empty vs empty is perfect; otherwise an empty side scores 0 on the ratio that divides by it.
        double p = pred.Count == 0 ? (gold.Count == 0 ? 1 : 0) : (double)matched / pred.Count;
        double r = gold.Count == 0 ? (pred.Count == 0 ? 1 : 0) : (double)matched / gold.Count;
        double f1 = p + r == 0 ? 0 : 2 * p * r / (p + r);
        return Result(p, r, f1, $"{matched} matched of {gold.Count} golden and {pred.Count} predicted lines.");
    }

    public static int Match(IReadOnlyList<LineItem> gold, IReadOnlyList<LineItem> pred)
    {
        var used = new bool[pred.Count];
        var matched = 0;
        foreach (var g in gold)
        {
            var best = -1;
            var bestOverlap = MinOverlap;
            for (var i = 0; i < pred.Count; i++)
            {
                if (used[i] || g.Amount is not { } ga || pred[i].Amount is not { } pa || Math.Abs(ga - pa) > 0.01m) continue;
                var overlap = Overlap(g.Description, pred[i].Description);
                if (overlap >= bestOverlap && (best < 0 || overlap > bestOverlap))
                {
                    best = i;
                    bestOverlap = overlap;
                }
            }
            if (best >= 0)
            {
                used[best] = true;
                matched++;
            }
        }
        return matched;
    }

    /// <summary>Jaccard overlap of lower-case alphanumeric tokens.</summary>
    public static double Overlap(string? a, string? b)
    {
        var ta = Tokens(a);
        var tb = Tokens(b);
        if (ta.Count == 0 && tb.Count == 0) return 1;
        return (double)ta.Intersect(tb).Count() / ta.Union(tb).Count();
    }

    private static HashSet<string> Tokens(string? s) =>
        [.. new string([.. (s ?? "").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ')])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    private static ValueTask<EvaluationResult> Result(double? p, double? r, double? f1, string reason) =>
        ValueTask.FromResult(new EvaluationResult(
            Metrics.Score(Precision, p, reason, passAt: 0.9),
            Metrics.Score(Recall, r, reason, passAt: 0.9),
            Metrics.Score(F1, f1, reason, passAt: 0.9)));
}
