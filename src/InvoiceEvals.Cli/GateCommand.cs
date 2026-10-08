using System.CommandLine;
using System.Text;

namespace InvoiceEvals.Cli;

/// <summary>
/// `evals gate`: fails (exit 1) when any gated metric of the latest summary dropped below the committed baseline by
/// more than its threshold. `--update-baseline` rewrites the baseline from the current summary instead; do that only
/// in a PR that explains why the numbers moved.
/// </summary>
internal static class GateCommand
{
    public static Command Create()
    {
        var updateBaseline = new Option<bool>("--update-baseline") { Description = "Rewrite evals/results/baseline.csv from the current summary and exit." };
        var markdownOut = new Option<FileInfo?>("--markdown-out") { Description = "Also write the before/after table to this file (for the PR comment)." };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };
        var command = new Command("gate", "Compare the current summary with the baseline; exit 1 on any regression beyond its threshold.") { updateBaseline, markdownOut, evalsDir };
        command.SetAction((r, ct) => RunAsync(r.GetValue(updateBaseline), r.GetValue(markdownOut), r.GetValue(evalsDir)!.FullName, ct));
        return command;
    }

    private static async Task<int> RunAsync(bool updateBaseline, FileInfo? markdownOut, string evalsDir, CancellationToken ct)
    {
        var thresholdsPath = Path.Combine(evalsDir, "thresholds.json");
        var summaryPath = Path.Combine(evalsDir, "results", "summary.csv");
        var baselinePath = Path.Combine(evalsDir, "results", "baseline.csv");
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            var thresholds = await Gate.LoadThresholdsAsync(thresholdsPath, ct);
            if (!File.Exists(summaryPath))
            {
                Console.Error.WriteLine($"{summaryPath} not found. Run `evals run` for every config, then `evals report`.");
                return 2;
            }
            var current = Gate.ReadLatestSummary(summaryPath, thresholds);

            if (updateBaseline)
            {
                await File.WriteAllTextAsync(baselinePath, Gate.BaselineCsv(current, thresholds), utf8, ct);
                Console.WriteLine($"Wrote {baselinePath} for {string.Join(", ", current.Configs.Order(StringComparer.Ordinal))}. Commit it in a PR that explains the change.");
                return 0;
            }
            if (!File.Exists(baselinePath))
            {
                Console.Error.WriteLine($"{baselinePath} not found. Create it with `evals gate --update-baseline`.");
                return 2;
            }

            var rows = Gate.Evaluate(Gate.ReadBaseline(baselinePath), current, thresholds);
            var markdown = Gate.Markdown(rows, "evals/results/baseline.csv", "evals/thresholds.json");
            Console.WriteLine(markdown);
            if (markdownOut is not null) await File.WriteAllTextAsync(markdownOut.FullName, markdown, utf8, ct);
            return rows.Any(r => r.Breach) ? 1 : 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or FormatException or IOException)
        {
            Console.Error.WriteLine($"Gate configuration error: {ex.Message}");
            return 2;
        }
    }
}
