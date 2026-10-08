using InvoiceEvals.Cli;

namespace InvoiceEvals.Tests;

public sealed class GateTests
{
    private static readonly Threshold[] Thresholds =
    [
        new("composite", "combined", 0.01),
        new("line_items_f1", "synthetic", 0.03),
    ];

    [Fact]
    public void Evaluate_DropWithinThreshold_Passes_BeyondThreshold_Breaches()
    {
        var baseline = Table(("mini-plain", "combined", "composite", 0.900), ("mini-plain", "synthetic", "line_items_f1", 0.800));
        var current = Table(("mini-plain", "combined", "composite", 0.890), ("mini-plain", "synthetic", "line_items_f1", 0.760));

        var rows = Gate.Evaluate(baseline, current, Thresholds);

        Assert.False(rows.Single(r => r.Metric == "composite").Breach); // Drop of exactly 0.01 is allowed.
        Assert.True(rows.Single(r => r.Metric == "line_items_f1").Breach);
    }

    [Fact]
    public void Evaluate_Improvement_Passes()
    {
        var rows = Gate.Evaluate(Table(("a", "combined", "composite", 0.5)), Table(("a", "combined", "composite", 0.9)), [Thresholds[0]]);

        Assert.False(Assert.Single(rows).Breach);
    }

    [Fact]
    public void Evaluate_BaselineConfigNotRun_Breaches_NewConfig_DoesNot()
    {
        var rows = Gate.Evaluate(Table(("old", "combined", "composite", 0.9)), Table(("new", "combined", "composite", 0.1)), [Thresholds[0]]);

        Assert.True(rows.Single(r => r.Config == "old").Breach);
        Assert.False(rows.Single(r => r.Config == "new").Breach);
    }

    [Fact]
    public void Evaluate_MetricBecameNull_Breaches_NullInBoth_Passes()
    {
        var baseline = Table(("a", "combined", "composite", 0.9), ("a", "synthetic", "line_items_f1", null));
        var current = Table(("a", "combined", "composite", null), ("a", "synthetic", "line_items_f1", null));

        var rows = Gate.Evaluate(baseline, current, Thresholds);

        Assert.True(rows.Single(r => r.Metric == "composite").Breach);
        Assert.False(rows.Single(r => r.Metric == "line_items_f1").Breach);
    }

    [Fact]
    public void Markdown_HasVerdictAndOneRowPerGatedCell()
    {
        var rows = Gate.Evaluate(Table(("a", "combined", "composite", 0.9)), Table(("a", "combined", "composite", 0.8)), [Thresholds[0]]);

        var md = Gate.Markdown(rows, "baseline.csv", "thresholds.json");

        Assert.StartsWith("### ❌ Eval gate failed: 1 breach", md, StringComparison.Ordinal);
        Assert.Contains("| `a` | composite | combined | 0.9000 | 0.8000 | -0.1000 | 0.0100 | ❌ regression |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_LatestExecutionPerConfig_AndBaselineRoundTrip()
    {
        var dir = Directory.CreateTempSubdirectory("gate-test").FullName;
        try
        {
            var summary = Path.Combine(dir, "summary.csv");
            File.WriteAllText(summary,
                "execution,config,subset,composite,line_items_f1\n" +
                "20260101T000000Z-aaa,mini-plain,combined,0.5000,\n" +
                "20260201T000000Z-bbb,mini-plain,combined,0.9000,\n" +
                "20260201T000000Z-bbb,mini-plain,synthetic,0.8000,0.7000\n");

            var current = Gate.ReadLatestSummary(summary, Thresholds);
            Assert.Equal(0.9, current[("mini-plain", "combined", "composite")]);
            Assert.Equal(0.7, current[("mini-plain", "synthetic", "line_items_f1")]);

            var baselinePath = Path.Combine(dir, "baseline.csv");
            File.WriteAllText(baselinePath, Gate.BaselineCsv(current, Thresholds));
            Assert.Equal("config,subset,metric,value\nmini-plain,combined,composite,0.9000\nmini-plain,synthetic,line_items_f1,0.7000\n", File.ReadAllText(baselinePath));
            Assert.All(Gate.Evaluate(Gate.ReadBaseline(baselinePath), current, Thresholds), r => Assert.False(r.Breach));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Summary_UnknownGatedColumn_IsAConfigurationError()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "execution,config,subset,composite\nx,a,combined,1\n");

            var ex = Assert.Throws<InvalidDataException>(() => Gate.ReadLatestSummary(path, [new("vendor_name_judgd_accuracy", "combined", 0.1)]));
            Assert.Contains("vendor_name_judgd_accuracy", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Csv_HandlesQuotesCommasAndCrLf()
    {
        var rows = Csv.Parse("a,b,c\r\n1,\"x, \"\"y\"\"\",\n");

        Assert.Equal(["a", "b", "c"], rows[0]);
        Assert.Equal(["1", "x, \"y\"", ""], rows[1]);
    }

    [Fact]
    public void CacheMissMessage_NamesDocumentsConfig_AndTellsTheAuthorWhatToDo()
    {
        var message = RunCommand.CacheMissMessage("mini-plain", ["fatura/T1_I1", "synthetic/discount-01"]);

        Assert.Contains("config 'mini-plain' on 2 document(s): fatura/T1_I1, synthetic/discount-01", message, StringComparison.Ordinal);
        Assert.Contains("run --config mini-plain", message, StringComparison.Ordinal);
        Assert.Contains("git add evals/cache", message, StringComparison.Ordinal);
    }

    private static MetricTable Table(params (string Config, string Subset, string Metric, double? Value)[] cells)
    {
        var table = new MetricTable();
        foreach (var c in cells) table[(c.Config, c.Subset, c.Metric)] = c.Value;
        return table;
    }
}
