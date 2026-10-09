using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Cli;

/// <summary>One stored scenario run, flattened for aggregation.</summary>
internal sealed record DocResult(
    string Execution,
    string Config,
    string Model,
    string Prompt,
    string InputMode,
    string DocId,
    string Source,
    string Layout,
    IReadOnlyDictionary<string, double?> Values,
    IReadOnlyDictionary<string, string> Outcomes,
    string Diagnostics,
    double LatencyMs,
    long InputTokens,
    long OutputTokens,
    long ThinkingTokens,
    bool CacheHit,
    string ResponseText)
{
    public double? this[string metric] => Values.GetValueOrDefault(metric);
}

internal static class StoredResults
{
    public const string ModelTag = "model:";
    public const string PromptTag = "prompt:";
    public const string InputTag = "input:";
    public const string SourceTag = "source:";
    public const string LayoutTag = "layout:";
    public const string JudgeTag = "judge:";

    public static async Task<IReadOnlyList<DocResult>> ReadAsync(string storePath, CancellationToken ct)
    {
        var results = new List<DocResult>();
        if (!Directory.Exists(storePath)) return results;
        await foreach (var r in new DiskBasedResultStore(storePath).ReadResultsAsync(cancellationToken: ct))
            results.Add(Flatten(r));
        return [.. results.OrderBy(r => r.Execution, StringComparer.Ordinal).ThenBy(r => r.Config, StringComparer.Ordinal).ThenBy(r => r.DocId, StringComparer.Ordinal)];
    }

    /// <summary>Inverts <see cref="InvoiceEvals.Core.EvalDocument.ScenarioName"/>: ids have exactly one "/", file names have no ".".</summary>
    private static string DocId(string scenarioName)
    {
        var i = scenarioName.IndexOf('.', StringComparison.Ordinal);
        return i < 0 ? scenarioName : $"{scenarioName[..i]}/{scenarioName[(i + 1)..]}";
    }

    private static DocResult Flatten(ScenarioRunResult r)
    {
        var tags = r.Tags ?? [];
        string Tag(string prefix) => tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..] ?? "";
        var metrics = r.EvaluationResult.Metrics.Values;
        var usage = r.ModelResponse.Usage;
        var turn = r.ChatDetails?.TurnDetails?.FirstOrDefault();
        return new DocResult(
            r.ExecutionName, r.IterationName, Tag(ModelTag), Tag(PromptTag), Tag(InputTag),
            DocId(r.ScenarioName), Tag(SourceTag), Tag(LayoutTag),
            metrics.OfType<NumericMetric>().ToDictionary(m => m.Name, m => m.Value, StringComparer.Ordinal),
            metrics.Where(m => m.Metadata?.ContainsKey(FieldAccuracyEvaluator.OutcomeKey) == true)
                .ToDictionary(m => m.Name, m => m.Metadata![FieldAccuracyEvaluator.OutcomeKey], StringComparer.Ordinal),
            string.Join(" | ", metrics.SelectMany(m => m.Diagnostics ?? []).Where(d => d.Severity == EvaluationDiagnosticSeverity.Error).Select(d => d.Message)),
            (LatencyStampingChatClient.ReadLatency(r.ModelResponse) ?? turn?.Latency ?? TimeSpan.Zero).TotalMilliseconds,
            usage?.InputTokenCount ?? 0, usage?.OutputTokenCount ?? 0, usage?.ReasoningTokenCount ?? 0,
            turn?.CacheHit == true,
            ExtractionAnswer.Text(r.ModelResponse));
    }
}
