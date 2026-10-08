using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Cli;

/// <summary>`evals run`: extracts every eval-set document with one configuration, scores it and stores the results.</summary>
internal static class RunCommand
{
    // Dry-run heuristics: characters per token, and per-request overhead for the injected JSON schema.
    private const double CharsPerToken = 3.5;
    private const int SchemaOverheadTokens = 600;
    private const double EstimateMargin = 1.2;

    private const int Concurrency = 4;

    public static Command Create()
    {
        var config = new Option<string>("--config") { Description = "Configuration name from evals/configs.json.", Required = true };
        var limit = new Option<int?>("--limit") { Description = "Only the first N documents (ordered by id)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print document count, estimated tokens and cost, then exit." };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };
        var promptsDir = new Option<DirectoryInfo>("--prompts-dir") { DefaultValueFactory = _ => new("prompts") };

        var command = new Command("run", "Extract and score the eval set with one configuration.") { config, limit, dryRun, evalsDir, promptsDir };
        command.SetAction((r, ct) => RunAsync(r.GetValue(config)!, r.GetValue(limit), r.GetValue(dryRun), r.GetValue(evalsDir)!.FullName, r.GetValue(promptsDir)!.FullName, ct));
        return command;
    }

    private static async Task<int> RunAsync(string configName, int? limit, bool dryRun, string evalsDir, string promptsDir, CancellationToken ct)
    {
        var config = await RunConfig.LoadAsync(Path.Combine(evalsDir, "configs.json"), configName, ct);
        var prompt = await PromptTemplate.LoadAsync(promptsDir, config.Prompt, ct);
        var docs = (await EvalSet.LoadAsync(evalsDir, config.InputMode, ct)).Take(limit ?? int.MaxValue).ToList();
        var prices = await ModelPrice.LoadAsync(Path.Combine(evalsDir, "pricing.json"), ct);
        var price = prices.GetValueOrDefault(config.Model) ?? throw new InvalidOperationException($"No price for '{config.Model}' in evals/pricing.json.");

        Console.WriteLine($"Config {config.Name}: model {config.Model}, prompt {prompt.Name}@{prompt.Version}, input {config.InputMode}, " +
            $"temperature {config.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "default"}{(config.DisableThinking ? ", thinking off" : "")}");
        Console.WriteLine($"Documents: {docs.Count} ({docs.Count(d => d.Golden.Source == "fatura")} FATURA, {docs.Count(d => d.Golden.Source == "synthetic")} synthetic)");

        if (dryRun)
        {
            PrintEstimate(docs, prompt, price);
            return 0;
        }

        var provider = new LatencyStampingChatClient(AnthropicChatClientFactory.Create(config.Model));
        var executionName = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{GitShortSha()}";
        var reporting = new ReportingConfiguration(
            [new CompositeScoreEvaluator()],
            new DiskBasedResultStore(Path.Combine(evalsDir, "results", "store")),
            new ChatConfiguration(provider),
            new DiskBasedResponseCacheProvider(Path.Combine(evalsDir, "cache"), timeToLiveForCacheEntries: TimeSpan.FromDays(3650)),
            executionName: executionName,
            tags: [$"{StoredResults.ModelTag}{config.Model}", $"{StoredResults.PromptTag}{prompt.Name}@{prompt.Version}", $"{StoredResults.InputTag}{config.InputMode}"]);
        Console.WriteLine($"Execution: {executionName}");

        var done = 0;
        var failures = new List<string>();
        await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (doc, token) =>
        {
            try
            {
                await using var run = await reporting.CreateScenarioRunAsync(
                    doc.ScenarioName, config.Name, additionalTags: [$"{StoredResults.SourceTag}{doc.Golden.Source}", $"{StoredResults.LayoutTag}{doc.Golden.Layout}"], cancellationToken: token);
                var result = await new ChatInvoiceExtractor(run.ChatConfiguration!.ChatClient, config, prompt).ExtractAsync(doc, token);
                var evaluation = await run.EvaluateAsync(result.Messages, result.Response, [new GoldenInvoiceContext(doc.Golden.Expected)], token);
                var composite = evaluation.Get<NumericMetric>(CompositeScoreEvaluator.MetricName).Value;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{Interlocked.Increment(ref done),3}/{docs.Count}] {doc.Id,-42} composite {composite:0.000}  {result.Latency.TotalMilliseconds,6:0} ms{(result.ParseError is null ? "" : "  PARSE ERROR")}"));
            }
            catch (Exception ex) when (ex is HttpRequestException or Anthropic.Exceptions.AnthropicApiException or TaskCanceledException && !token.IsCancellationRequested)
            {
                lock (failures) failures.Add($"{doc.Id}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[{Interlocked.Increment(ref done),3}/{docs.Count}] {doc.Id,-42} FAILED after {AnthropicChatClientFactory.MaxRetries} retries: {ex.Message}");
            }
        });

        Console.WriteLine($"Network calls: {provider.NetworkCalls}, served from cache: {docs.Count - failures.Count - provider.NetworkCalls}, failed: {failures.Count}");
        failures.ForEach(f => Console.WriteLine($"  {f}"));
        return failures.Count == 0 ? 0 : 1;
    }

    private static void PrintEstimate(List<EvalDocument> docs, PromptTemplate prompt, ModelPrice price)
    {
        var input = docs.Sum(d => ((prompt.Body.Length + d.Text.Length) / CharsPerToken) + SchemaOverheadTokens) * EstimateMargin;
        var output = docs.Sum(d => JsonSerializer.Serialize(d.Golden.Expected, GoldenSet.JsonOptions).Length / CharsPerToken) * EstimateMargin;
        var cost = price.Cost(input, output);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Estimated tokens: {input:N0} input, {output:N0} output ({input / Math.Max(1, docs.Count):N0} / {output / Math.Max(1, docs.Count):N0} per document, +{EstimateMargin - 1:P0} margin)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Estimated cost: ${cost:0.000} at ${price.InputPerMTok}/${price.OutputPerMTok} per MTok (${cost / Math.Max(1, docs.Count) * 1000:0.00} per 1k documents). Cached documents cost nothing."));
    }

    private static string GitShortSha()
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, UseShellExecute = false })!;
            var sha = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && sha.Length > 0 ? sha : "nogit";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "nogit";
        }
    }
}
