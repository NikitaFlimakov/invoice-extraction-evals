using System.CommandLine;
using System.Globalization;

using InvoiceEvals.Evaluation;

namespace InvoiceEvals.Cli;

/// <summary>`evals compare`: per-document composite deltas (b − a) between the latest executions of two configurations.</summary>
internal static class CompareCommand
{
    public static Command Create()
    {
        var a = new Option<string>("--a") { Description = "Baseline configuration.", Required = true };
        var b = new Option<string>("--b") { Description = "Candidate configuration.", Required = true };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };
        var command = new Command("compare", "Compare composite scores of two configurations document by document.") { a, b, evalsDir };
        command.SetAction((r, ct) => RunAsync(r.GetValue(a)!, r.GetValue(b)!, r.GetValue(evalsDir)!.FullName, ct));
        return command;
    }

    private static async Task<int> RunAsync(string a, string b, string evalsDir, CancellationToken ct)
    {
        var results = await StoredResults.ReadAsync(Path.Combine(evalsDir, "results", "store"), ct);
        Dictionary<string, (double? Score, string Source)>? Latest(string config)
        {
            var runs = results.Where(r => r.Config == config).ToList();
            if (runs.Count == 0) return null;
            var execution = runs.Max(r => r.Execution);
            return runs.Where(r => r.Execution == execution).ToDictionary(r => r.DocId, r => (r[CompositeScoreEvaluator.MetricName], r.Source), StringComparer.Ordinal);
        }

        var (left, right) = (Latest(a), Latest(b));
        if (left is null || right is null)
        {
            Console.WriteLine($"No stored results for '{(left is null ? a : b)}'. Run `evals run --config ...` first.");
            return 1;
        }

        var deltas = left.Keys.Intersect(right.Keys)
            .Where(id => left[id].Score is not null && right[id].Score is not null)
            .Select(id => (Id: id, left[id].Source, A: left[id].Score!.Value, B: right[id].Score!.Value, Delta: right[id].Score!.Value - left[id].Score!.Value))
            .OrderBy(d => d.Delta).ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"{"document",-42} {a,10} {b,10} {"delta",8}");
        foreach (var d in deltas.Where(d => d.Delta != 0))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{d.Id,-42} {d.A,10:0.000} {d.B,10:0.000} {d.Delta,8:+0.000;-0.000}"));
        Console.WriteLine($"({deltas.Count(d => d.Delta == 0)} documents with zero delta not shown)");

        foreach (var subset in new[] { "fatura", "synthetic", "combined" })
        {
            var s = deltas.Where(d => subset == "combined" || d.Source == subset).ToList();
            if (s.Count == 0) continue;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Mean delta ({b} − {a}), {subset}: {s.Average(d => d.Delta):+0.0000;-0.0000} over {s.Count} documents ({s.Count(d => d.Delta > 0)} better, {s.Count(d => d.Delta < 0)} worse)"));
        }
        Console.WriteLine("No confidence interval yet: bootstrap CIs arrive in Phase 3.");
        return 0;
    }
}
