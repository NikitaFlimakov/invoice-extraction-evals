using System.CommandLine;
using System.Globalization;
using System.Text;

using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Cli;

/// <summary>
/// `evals judge pairs` builds evals/judge/calibration_pairs.jsonl for hand labelling; `evals judge calibrate` runs
/// the judge on the labelled pairs and writes calibration_report_v&lt;prompt version&gt;.md with Cohen's κ.
/// </summary>
internal static class JudgeCommand
{
    public const double TargetKappa = 0.75;

    private const int Concurrency = 4;
    private const double CharsPerToken = 3.5;
    private const int SchemaOverheadTokens = 300;
    private const int OutputTokens = 80;

    public static Command Create()
    {
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };

        var count = new Option<int>("--count") { Description = "Target number of pairs (real mismatches first, then synthetic).", DefaultValueFactory = _ => 60 };
        var seed = new Option<ulong>("--seed") { Description = "Seed for synthetic perturbations.", DefaultValueFactory = _ => 42 };
        var pairs = new Command("pairs", "Build the calibration set from gray-zone mismatches of the latest runs plus synthetic perturbations. Existing labels are kept by id.") { count, seed, evalsDir };
        pairs.SetAction((r, ct) => PairsAsync(r.GetValue(count), r.GetValue(seed), r.GetValue(evalsDir)!.FullName, ct));

        var dryRun = new Option<bool>("--dry-run") { Description = "Print the number of judge calls and estimated cost, then exit." };
        var calibrate = new Command("calibrate", "Run the judge on the labelled pairs and write the calibration report.") { dryRun, evalsDir };
        calibrate.SetAction((r, ct) => CalibrateAsync(r.GetValue(dryRun), r.GetValue(evalsDir)!.FullName, ct));

        return new Command("judge", "Vendor/customer-name judge calibration.") { pairs, calibrate };
    }

    private static string PairsPath(string evalsDir) => Path.Combine(evalsDir, "judge", "calibration_pairs.jsonl");

    private static async Task<int> PairsAsync(int count, ulong seed, string evalsDir, CancellationToken ct)
    {
        var docs = (await EvalSet.LoadAsync(evalsDir, EvalSet.TextMode, ct)).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var results = await StoredResults.ReadAsync(Path.Combine(evalsDir, "results", "store"), ct);
        var latest = results.GroupBy(r => r.Config).Select(g => g.Max(r => r.Execution)!).ToHashSet(StringComparer.Ordinal);

        var real = new List<CalibrationPairs.RealMismatch>();
        foreach (var r in results.Where(r => latest.Contains(r.Execution) && docs.ContainsKey(r.DocId)))
        {
            if (!InvoiceJson.TryParse(r.ResponseText, out var predicted, out _)) continue;
            var doc = docs[r.DocId];
            foreach (var field in FieldAccuracyEvaluator.JudgedFieldNames)
            {
                if (r.Outcomes.GetValueOrDefault(FieldAccuracyEvaluator.MetricName(field)) != FieldAccuracyEvaluator.Outcome.Mismatch) continue;
                // The vendor name is not in FATURA's OCR layer: those mismatches measure the dataset, and the judge is never asked.
                if (field == "vendor_name" && r.InputMode == EvalSet.OcrMode && doc.Golden.OcrTextPath is not null) continue;
                var (golden, actual) = field == "vendor_name"
                    ? (doc.Golden.Expected.Vendor?.Name, predicted!.Vendor?.Name)
                    : (doc.Golden.Expected.Customer?.Name, predicted!.Customer?.Name);
                if (golden is not null && actual is not null) real.Add(new(r.DocId, field, golden, actual, doc.TextLayer, r.Config));
            }
        }

        var sources = docs.Values.SelectMany(d => new[]
        {
            new CalibrationPairs.Source(d.Id, "vendor_name", d.Golden.Expected.Vendor?.Name ?? "", d.TextLayer),
            new CalibrationPairs.Source(d.Id, "customer_name", d.Golden.Expected.Customer?.Name ?? "", d.TextLayer),
        });
        var generated = CalibrationPairs.Generate(real, sources, count, seed);

        var path = PairsPath(evalsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var existing = File.Exists(path) ? await CalibrationPairs.ReadAsync(path, ct) : [];
        var merged = CalibrationPairs.KeepLabels(generated, existing);
        await CalibrationPairs.WriteAsync(path, merged, ct);

        var realCount = merged.Count(p => p.Origin.StartsWith("run:", StringComparison.Ordinal));
        Console.WriteLine($"Wrote {merged.Count} pairs to {path}: {realCount} real gray-zone mismatches from {latest.Count} configs, {merged.Count - realCount} synthetic.");
        foreach (var g in merged.GroupBy(p => p.Origin.StartsWith("run:", StringComparison.Ordinal) ? "real" : p.Origin).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {g.Key}: {g.Count()}");
        var dropped = existing.Count(e => e.HumanLabel is not null && merged.All(m => m.Id != e.Id));
        Console.WriteLine($"Labelled: {merged.Count(p => p.HumanLabel is not null)}{(dropped > 0 ? $"; {dropped} previously labelled pairs are no longer generated and were dropped" : "")}.");
        if (latest.Count == 0)
            Console.WriteLine("No stored runs: only synthetic pairs. Run `evals run` for every config first, then regenerate (labels are kept by id).");
        return 0;
    }

    private static async Task<int> CalibrateAsync(bool dryRun, string evalsDir, CancellationToken ct)
    {
        var judge = new NameJudge(await PromptTemplate.LoadAsync(Path.Combine(evalsDir, "judge"), "judge_prompt", ct));
        var pairs = await CalibrationPairs.ReadAsync(PairsPath(evalsDir), ct);
        var prices = await ModelPrice.LoadAsync(Path.Combine(evalsDir, "pricing.json"), ct);
        var price = prices.GetValueOrDefault(judge.Model) ?? throw new InvalidOperationException($"No price for '{judge.Model}' in evals/pricing.json.");
        Console.WriteLine($"Judge {judge.Model}, judge_prompt@{judge.PromptVersion}, {pairs.Count} pairs");

        if (dryRun)
        {
            var input = pairs.Sum(p => (judge.Messages(p.Field, p.Golden, p.Predicted, p.Excerpt).Sum(m => m.Text.Length) / CharsPerToken) + SchemaOverheadTokens);
            var output = pairs.Count * OutputTokens;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Estimated: {pairs.Count} calls, {input:N0} input + {output:N0} output tokens, ${price.Cost(input, output):0.0000}. Cached pairs cost nothing."));
            return 0;
        }

        var unlabelled = pairs.Count(p => p.HumanLabel is null);
        if (unlabelled > 0)
        {
            Console.Error.WriteLine($"{unlabelled} of {pairs.Count} pairs have no human_label. Label every pair (true = same party) first.");
            return 2;
        }

        IChatClient chat;
        try
        {
            var cache = await new DiskBasedResponseCacheProvider(Path.Combine(evalsDir, "cache"), TimeSpan.FromDays(3650))
                .GetCacheAsync("judge-calibration", $"judge_prompt@{judge.PromptVersion}", ct);
            chat = new DistributedCachingChatClient(new LatencyStampingChatClient(AnthropicChatClientFactory.Create(judge.Model)), cache);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var verdicts = new NameJudgement[pairs.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, pairs.Count), new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (i, token) =>
        {
            var p = pairs[i];
            // The stored excerpt is what the labeller saw; the judge reads the same text.
            verdicts[i] = await judge.JudgeAsync(chat, p.Field, p.Golden, p.Predicted, p.Excerpt, token);
        });

        var report = Report(judge, pairs, verdicts);
        var path = Path.Combine(evalsDir, "judge", $"calibration_report_v{judge.PromptVersion}.md");
        await File.WriteAllTextAsync(path, report.Markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"κ = {report.Kappa.Kappa:0.000} (target ≥ {TargetKappa}); wrote {path}"));
        return 0;
    }

    internal sealed record CalibrationReport(CohenKappa Kappa, string Markdown);

    /// <summary>An unparsed verdict counts as "not equivalent", which is how the evaluator scores it.</summary>
    internal static CalibrationReport Report(NameJudge judge, IReadOnlyList<CalibrationPair> pairs, IReadOnlyList<NameJudgement> verdicts)
    {
        var rows = pairs.Select((p, i) => (Pair: p, Human: p.HumanLabel!.Value, Judge: verdicts[i].Equivalent ?? false, Verdict: verdicts[i])).ToList();
        var kappa = CohenKappa.From(rows.Select(r => (r.Human, r.Judge)));
        var met = kappa.Kappa >= TargetKappa;

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"# Judge calibration: judge_prompt v{judge.PromptVersion}\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"Generated by `evals judge calibrate`. Judge `{judge.Model}`, temperature 0, prompt [`evals/judge/judge_prompt.md`](judge_prompt.md) v{judge.PromptVersion}, ");
        sb.Append(CultureInfo.InvariantCulture, $"{pairs.Count} hand-labelled pairs from [`calibration_pairs.jsonl`](calibration_pairs.jsonl) ");
        sb.Append(CultureInfo.InvariantCulture, $"({rows.Count(r => r.Pair.Origin.StartsWith("run:", StringComparison.Ordinal))} real gray-zone mismatches, {rows.Count(r => r.Pair.Origin.StartsWith("synthetic:", StringComparison.Ordinal))} synthetic). ");
        sb.Append("The prompt may be revised on this set at most once; a revision bumps the version and gets its own report, and earlier reports are kept.\n\n");

        sb.Append("| | Value |\n|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Cohen's κ | **{(double.IsNaN(kappa.Kappa) ? "undefined" : kappa.Kappa.ToString("0.000", CultureInfo.InvariantCulture))}** (target ≥ {TargetKappa}: {(met ? "met" : "**not met**")}) |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Observed agreement | {kappa.Observed:0.0%} |\n| Chance agreement | {kappa.Expected:0.0%} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Human-labelled equivalent | {rows.Count(r => r.Human)} of {rows.Count} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| Unparsed judge responses | {rows.Count(r => r.Verdict.Equivalent is null)} |\n\n");

        sb.Append("## Confusion matrix\n\n| | Judge: equivalent | Judge: different |\n|---|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| **Human: equivalent** | {kappa.BothTrue} | {kappa.HumanOnly} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| **Human: different** | {kappa.JudgeOnly} | {kappa.BothFalse} |\n\n");

        sb.Append("## Agreement by origin\n\n| Origin | Pairs | Human equivalent | Judge equivalent | Agreement | Judge too lenient | Judge too strict |\n|---|---|---|---|---|---|---|\n");
        foreach (var g in rows.GroupBy(r => r.Pair.Origin.StartsWith("run:", StringComparison.Ordinal) ? "real (runs)" : r.Pair.Origin["synthetic:".Length..]).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"| {g.Key} | {g.Count()} | {g.Count(r => r.Human)} | {g.Count(r => r.Judge)} | {(double)g.Count(r => r.Human == r.Judge) / g.Count():0%} | {g.Count(r => r.Judge && !r.Human)} | {g.Count(r => !r.Judge && r.Human)} |\n");
        }

        var disagreements = rows.Where(r => r.Human != r.Judge).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"\n## Disagreements ({disagreements.Count})\n\n");
        if (disagreements.Count == 0)
        {
            sb.Append("None.\n");
        }
        else
        {
            sb.Append("| Pair | Field | Origin | Annotated | Extracted | Human | Judge | Judge's reason |\n|---|---|---|---|---|---|---|---|\n");
            foreach (var r in disagreements)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"| `{r.Pair.Id}` | {r.Pair.Field} | {r.Pair.Origin} | {Cell(r.Pair.Golden)} | {Cell(r.Pair.Predicted)} | {Same(r.Human)} | {Same(r.Judge)}{(r.Verdict.Equivalent is null ? " (unparsed)" : "")} | {Cell(r.Verdict.Reason)} |\n");
            }
        }

        sb.Append("\n## Systematic patterns\n\n_Written by hand after reading the disagreements above._\n");
        return new CalibrationReport(kappa, sb.ToString());
    }

    private static string Same(bool equivalent) => equivalent ? "same" : "different";

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
