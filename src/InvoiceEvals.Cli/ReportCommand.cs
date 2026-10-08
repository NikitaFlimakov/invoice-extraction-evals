using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;

using InvoiceEvals.Evaluation;

namespace InvoiceEvals.Cli;

/// <summary>
/// `evals report`: aggregates every stored execution into evals/results/summary.csv (+ summary.md), one CSV per
/// execution, docs/results-by-layout.md, and the aieval HTML report in docs/report/index.html.
/// </summary>
internal static class ReportCommand
{
    private static readonly string[] Subsets = ["fatura", "synthetic", "combined"];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static Command Create()
    {
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };
        var docsDir = new Option<DirectoryInfo>("--docs-dir") { DefaultValueFactory = _ => new("docs") };
        var command = new Command("report", "Aggregate stored results into CSV, Markdown and HTML reports.") { evalsDir, docsDir };
        command.SetAction((r, ct) => RunAsync(r.GetValue(evalsDir)!.FullName, r.GetValue(docsDir)!.FullName, ct));
        return command;
    }

    private static async Task<int> RunAsync(string evalsDir, string docsDir, CancellationToken ct)
    {
        var storePath = Path.Combine(evalsDir, "results", "store");
        var results = await StoredResults.ReadAsync(storePath, ct);
        if (results.Count == 0)
        {
            Console.WriteLine($"No results in {storePath}. Run `evals run` first.");
            return 1;
        }
        var prices = await ModelPrice.LoadAsync(Path.Combine(evalsDir, "pricing.json"), ct);

        var rows = results.GroupBy(r => (r.Execution, r.Config))
            .SelectMany(g => Subsets.Select(s => Summarize(g.Key.Execution, g.Key.Config, s, [.. g.Where(r => s == "combined" || r.Source == s)], prices)))
            .Where(row => row.Docs > 0)
            .ToList();

        var resultsDir = Path.Combine(evalsDir, "results");
        await WriteCsvAsync(Path.Combine(resultsDir, "summary.csv"), SummaryRow.Header, rows.Select(r => r.Cells()), ct);
        foreach (var execution in results.GroupBy(r => r.Execution))
            await WriteCsvAsync(Path.Combine(resultsDir, $"{execution.Key}.csv"), DocumentHeader, execution.Select(DocumentCells), ct);

        // Baseline = the latest execution per configuration.
        var latest = rows.GroupBy(r => r.Config).Select(g => g.Max(r => r.Execution)!).ToHashSet(StringComparer.Ordinal);
        var baseline = rows.Where(r => latest.Contains(r.Execution)).ToList();
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "summary.md"), BaselineMarkdown(baseline), Utf8, ct);
        await File.WriteAllTextAsync(Path.Combine(docsDir, "results-by-layout.md"),
            LayoutMarkdown([.. results.Where(r => latest.Contains(r.Execution))]), Utf8, ct);
        Console.WriteLine($"Wrote summary.csv ({rows.Count} rows), {results.Select(r => r.Execution).Distinct().Count()} per-execution CSVs, summary.md, results-by-layout.md");

        return await AiEvalReportAsync(storePath, Path.Combine(docsDir, "report", "index.html"), ct);
    }

    private static SummaryRow Summarize(string execution, string config, string subset, List<DocResult> docs, IReadOnlyDictionary<string, ModelPrice> prices)
    {
        double? Mean(IEnumerable<double?> values) => values.OfType<double>().ToList() is { Count: > 0 } v ? v.Average() : null;
        double? Rate(string field, string outcome, params string[] population)
        {
            var outcomes = docs.Select(d => d.Outcomes.GetValueOrDefault(FieldAccuracyEvaluator.MetricName(field))).Where(population.Contains).ToList();
            return outcomes.Count == 0 ? null : (double)outcomes.Count(o => o == outcome) / outcomes.Count;
        }

        var latencies = docs.Select(d => d.LatencyMs).Order().ToList();
        var model = docs[0].Model;
        var price = prices.GetValueOrDefault(model);
        var meanIn = docs.Average(d => (double)d.InputTokens);
        var meanOut = docs.Average(d => (double)d.OutputTokens);
        var fields = FieldAccuracyEvaluator.FieldNames;
        return new SummaryRow(
            execution, config, model, docs[0].Prompt, docs[0].InputMode, subset, docs.Count,
            Mean(docs.Select(d => d[SchemaValidityEvaluator.MetricName])),
            [.. fields.Select(f => Mean(docs.Select(d => d[FieldAccuracyEvaluator.MetricName(f)])))],
            [.. fields.Select(f => Rate(f, FieldAccuracyEvaluator.Outcome.FalsePositive, FieldAccuracyEvaluator.Outcome.FalsePositive, FieldAccuracyEvaluator.Outcome.CorrectNull))],
            [.. fields.Select(f => Rate(f, FieldAccuracyEvaluator.Outcome.Miss, FieldAccuracyEvaluator.Outcome.Miss, FieldAccuracyEvaluator.Outcome.CorrectValue, FieldAccuracyEvaluator.Outcome.Mismatch))],
            Mean(docs.Select(d => d[RecomputedTotalEvaluator.MetricName])), docs.Count(d => d[RecomputedTotalEvaluator.MetricName] is not null),
            Mean(docs.Select(d => d[LineItemsF1Evaluator.Precision])), Mean(docs.Select(d => d[LineItemsF1Evaluator.Recall])),
            Mean(docs.Select(d => d[LineItemsF1Evaluator.F1])), docs.Count(d => d[LineItemsF1Evaluator.F1] is not null),
            Mean(docs.Select(d => d[CompositeScoreEvaluator.MetricName])),
            latencies.Average(), Percentile(latencies, 0.50), Percentile(latencies, 0.95),
            meanIn, meanOut, docs.Average(d => (double)d.ThinkingTokens),
            price is null ? null : (double)price.Cost(meanIn, meanOut) * 1000);
    }

    /// <summary>Nearest-rank percentile on a sorted list.</summary>
    private static double Percentile(List<double> sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Count) - 1)];

    private sealed record SummaryRow(
        string Execution, string Config, string Model, string Prompt, string InputMode, string Subset, int Docs,
        double? SchemaValidity, double?[] FieldAccuracy, double?[] FieldFalsePositiveRate, double?[] FieldMissRate,
        double? RecomputedTotalRate, int RecomputedTotalN,
        double? LineItemsPrecision, double? LineItemsRecall, double? LineItemsF1, int LineItemsCoverage,
        double? Composite, double LatencyMeanMs, double LatencyP50Ms, double LatencyP95Ms,
        double InputTokensMean, double OutputTokensMean, double ThinkingTokensMean, double? UsdPer1kDocs)
    {
        public static readonly string[] Header =
        [
            "execution", "config", "model", "prompt", "input_mode", "subset", "docs", "schema_validity",
            .. FieldAccuracyEvaluator.FieldNames.Select(f => $"{f}_accuracy"),
            .. FieldAccuracyEvaluator.FieldNames.Select(f => $"{f}_false_positive_rate"),
            .. FieldAccuracyEvaluator.FieldNames.Select(f => $"{f}_miss_rate"),
            "recomputed_total_rate", "recomputed_total_n",
            "line_items_precision", "line_items_recall", "line_items_f1", "line_items_coverage",
            "composite", "latency_mean_ms", "latency_p50_ms", "latency_p95_ms",
            "input_tokens_mean", "output_tokens_mean", "thinking_tokens_mean", "usd_per_1k_docs",
        ];

        public IEnumerable<string> Cells() =>
        [
            Execution, Config, Model, Prompt, InputMode, Subset, Docs.ToString(CultureInfo.InvariantCulture), F(SchemaValidity),
            .. FieldAccuracy.Select(F), .. FieldFalsePositiveRate.Select(F), .. FieldMissRate.Select(F),
            F(RecomputedTotalRate), RecomputedTotalN.ToString(CultureInfo.InvariantCulture),
            F(LineItemsPrecision), F(LineItemsRecall), F(LineItemsF1), LineItemsCoverage.ToString(CultureInfo.InvariantCulture),
            F(Composite), F(LatencyMeanMs, "0"), F(LatencyP50Ms, "0"), F(LatencyP95Ms, "0"),
            F(InputTokensMean, "0.0"), F(OutputTokensMean, "0.0"), F(ThinkingTokensMean, "0.0"), F(UsdPer1kDocs, "0.00"),
        ];

        public double? Field(string name) => FieldAccuracy[FieldAccuracyEvaluator.FieldNames.ToList().IndexOf(name)];
    }

    private static readonly string[] DocumentHeader =
    [
        "execution", "config", "doc_id", "source", "layout", "composite", "schema_validity",
        .. FieldAccuracyEvaluator.FieldNames,
        "recomputed_total", "line_items_precision", "line_items_recall", "line_items_f1",
        "latency_ms", "input_tokens", "output_tokens", "thinking_tokens", "cache_hit", "errors",
    ];

    private static IEnumerable<string> DocumentCells(DocResult d) =>
    [
        d.Execution, d.Config, d.DocId, d.Source, d.Layout, F(d[CompositeScoreEvaluator.MetricName]), F(d[SchemaValidityEvaluator.MetricName]),
        .. FieldAccuracyEvaluator.FieldNames.Select(f => d.Outcomes.GetValueOrDefault(FieldAccuracyEvaluator.MetricName(f)) ?? ""),
        F(d[RecomputedTotalEvaluator.MetricName]), F(d[LineItemsF1Evaluator.Precision]), F(d[LineItemsF1Evaluator.Recall]), F(d[LineItemsF1Evaluator.F1]),
        F(d.LatencyMs, "0"), I(d.InputTokens), I(d.OutputTokens), I(d.ThinkingTokens), d.CacheHit ? "true" : "false", d.Diagnostics,
    ];

    private static string BaselineMarkdown(List<SummaryRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("| Configuration | Subset | Docs | Schema valid | Invoice no. | Dates | Total | Currency | Vendor name | Recomputed total | Line-items F1 | Composite | Thinking tok/doc | $/1k docs | p50 / p95 ms |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in rows.OrderBy(r => ConfigOrder(r.Config)).ThenBy(r => Array.IndexOf(Subsets, r.Subset)))
        {
            var dates = r.Field("invoice_date") is { } a && r.Field("due_date") is { } b ? (a + b) / 2 : (double?)null;
            var subset = r.Subset switch
            {
                "fatura" => "FATURA",
                "synthetic" when r.InputMode == "ocr" => "synthetic (text layer)",
                var s => s,
            };
            sb.Append(CultureInfo.InvariantCulture, $"| `{r.Config}` | {subset} | {r.Docs} | {P(r.SchemaValidity)} | {P(r.Field("invoice_number"))} | {P(dates)} | {P(r.Field("total"))} | {P(r.Field("currency"))} | {P(r.Field("vendor_name"))} | ");
            sb.Append(CultureInfo.InvariantCulture, $"{(r.RecomputedTotalN == 0 ? "n/a" : $"{P(r.RecomputedTotalRate)} (n={r.RecomputedTotalN})")} | {(r.LineItemsCoverage == 0 ? "n/a" : $"{r.LineItemsF1:0.00} (n={r.LineItemsCoverage})")} | ");
            sb.Append(CultureInfo.InvariantCulture, $"{r.Composite:0.000} | {r.ThinkingTokensMean:0} | {r.UsdPer1kDocs:0.00} | {r.LatencyP50Ms:0} / {r.LatencyP95Ms:0} |\n");
        }
        return sb.ToString();
    }

    private static string LayoutMarkdown(List<DocResult> results)
    {
        var configs = results.Select(r => r.Config).Distinct().OrderBy(ConfigOrder).ToList();
        var sb = new StringBuilder();
        sb.Append("# Results by layout\n\nGenerated by `evals report` from the latest execution of each configuration. ");
        sb.Append("Cells are mean composite score (see [metrics.md](metrics.md)); FATURA layouts have 3–4 documents each, synthetic families 4–5.\n\n");
        sb.Append("| Source | Layout | Docs | ").Append(string.Join(" | ", configs.Select(c => $"`{c}`"))).Append(" |\n");
        sb.Append("|---|---|---|").Append(string.Concat(configs.Select(_ => "---|"))).Append('\n');
        foreach (var g in results.GroupBy(r => (r.Source, r.Layout)).OrderBy(g => g.Key.Source, StringComparer.Ordinal).ThenBy(g => LayoutOrder(g.Key.Layout)))
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {g.Key.Source} | {g.Key.Layout} | {g.Select(r => r.DocId).Distinct().Count()} | ");
            sb.Append(string.Join(" | ", configs.Select(c => g.Where(r => r.Config == c).Select(r => r[CompositeScoreEvaluator.MetricName]).OfType<double>().ToList() is { Count: > 0 } v
                ? v.Average().ToString("0.000", CultureInfo.InvariantCulture) : "–")));
            sb.Append(" |\n");
        }
        return sb.ToString();
    }

    private static async Task<int> AiEvalReportAsync(string storePath, string output, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var start = new ProcessStartInfo("dotnet", ["aieval", "report", "--path", storePath, "--output", output, "-n", "100"]) { UseShellExecute = false };
        start.Environment["DOTNET_AIEVAL_TELEMETRY_OPTOUT"] = "1";
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            Console.WriteLine("`dotnet aieval report` failed. Run `dotnet tool restore` and retry.");
            return process.ExitCode;
        }
        Console.WriteLine($"Wrote {output}");
        return 0;
    }

    private static async Task WriteCsvAsync(string path, IEnumerable<string> header, IEnumerable<IEnumerable<string>> rows, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var row in rows.Prepend(header))
            sb.Append(string.Join(',', row.Select(Csv))).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), Utf8, ct);
    }

    private static string Csv(string cell) =>
        cell.IndexOfAny([',', '"', '\n', '\r']) < 0 ? cell : $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal).ReplaceLineEndings(" ")}\"";

    internal static int ConfigOrder(string config) => Array.IndexOf(["mini-plain", "mini-fewshot", "mini-plain-ocr", "strong-plain"], config) is var i and >= 0 ? i : 99;

    private static (int, string) LayoutOrder(string layout) =>
        layout.StartsWith("Template", StringComparison.Ordinal) && int.TryParse(layout.AsSpan(8), CultureInfo.InvariantCulture, out var n) ? (n, "") : (0, layout);

    private static string F(double? v) => F(v, "0.0000");

    private static string F(double? v, string format) => v?.ToString(format, CultureInfo.InvariantCulture) ?? "";

    private static string I(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string P(double? v) => v is { } x ? x.ToString("0.0%", CultureInfo.InvariantCulture).Replace(" ", "", StringComparison.Ordinal) : "n/a";
}
