using InvoiceEvals.Cli;
using InvoiceEvals.Evaluation;

namespace InvoiceEvals.Tests;

public sealed class CompareTests
{
    [Fact]
    public void Rows_SubsetsWithOppositeSigns_AreSplit_OtherwiseCombinedOnly()
    {
        // Composite: FATURA improves, synthetic gets worse. Total: both improve.
        var left = Docs(("fatura/a", "fatura", 0.5, 0), ("fatura/b", "fatura", 0.5, 0), ("synthetic/a", "synthetic", 0.9, 0));
        var right = Docs(("fatura/a", "fatura", 0.7, 1), ("fatura/b", "fatura", 0.7, 1), ("synthetic/a", "synthetic", 0.8, 1));

        var rows = CompareCommand.Rows(left, right, resamples: 1000, seed: 42);

        Assert.Equal(["fatura", "synthetic", "combined"], rows.Where(r => r.Metric == CompositeScoreEvaluator.MetricName).Select(r => r.Subset));
        var total = Assert.Single(rows, r => r.Metric == FieldAccuracyEvaluator.MetricName("total"));
        Assert.Equal(("combined", 3, 1.0), (total.Subset, total.Interval.N, total.Interval.Mean));
    }

    [Fact]
    public void Rows_MetricInOneSubsetOnly_IsLabelledWithThatSubset_UnpairedDocsExcluded()
    {
        var left = Docs(("synthetic/a", "synthetic", 0.9, 1), ("fatura/a", "fatura", 0.5, 1), ("fatura/only-left", "fatura", 0.1, 0));
        var right = Docs(("synthetic/a", "synthetic", 0.9, 1), ("fatura/a", "fatura", 0.5, 1));
        left["synthetic/a"] = With(left["synthetic/a"], LineItemsF1Evaluator.F1, 0.5);
        right["synthetic/a"] = With(right["synthetic/a"], LineItemsF1Evaluator.F1, 1.0);

        var rows = CompareCommand.Rows(left, right, 1000, 42);

        var f1 = Assert.Single(rows, r => r.Metric == LineItemsF1Evaluator.F1);
        Assert.Equal(("synthetic", 1, 0.5), (f1.Subset, f1.Interval.N, f1.Interval.Mean));
        Assert.Equal(2, rows.Single(r => r.Metric == CompositeScoreEvaluator.MetricName).Interval.N);
    }

    private static Dictionary<string, DocResult> Docs(params (string Id, string Source, double Composite, double Total)[] docs) =>
        docs.ToDictionary(d => d.Id, d => new DocResult(
            "exec", "cfg", "m", "p", "text", d.Id, d.Source, "L",
            new Dictionary<string, double?>(StringComparer.Ordinal)
            {
                [CompositeScoreEvaluator.MetricName] = d.Composite,
                [FieldAccuracyEvaluator.MetricName("total")] = d.Total,
            },
            new Dictionary<string, string>(), "", 0, 0, 0, 0, false, ""), StringComparer.Ordinal);

    private static DocResult With(DocResult doc, string metric, double value) =>
        doc with { Values = new Dictionary<string, double?>(doc.Values, StringComparer.Ordinal) { [metric] = value } };
}
