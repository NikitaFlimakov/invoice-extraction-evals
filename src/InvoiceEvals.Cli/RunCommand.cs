using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using InvoiceEvals.Agent;
using InvoiceEvals.Cli.Langfuse;
using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Cli;

/// <summary>
/// `evals run`: extracts every eval-set document with one configuration, scores it (with the vendor/customer-name
/// judge in the gray zone) and stores the results. With LANGFUSE_* set, the run is also a Langfuse experiment.
/// </summary>
internal static class RunCommand
{
    // Dry-run heuristics: characters per token, and per-request overhead for the injected JSON schema.
    private const double CharsPerToken = 3.5;
    private const int SchemaOverheadTokens = 600;
    private const double EstimateMargin = 1.2;
    private const int JudgeOutputTokens = 80;

    private const int Concurrency = 4;
    private const int MaxListedFailures = 20;

    public static Command Create()
    {
        var config = new Option<string>("--config") { Description = "Configuration name from evals/configs.json.", Required = true };
        var limit = new Option<int?>("--limit") { Description = "Only the first N documents (ordered by id)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print document count, estimated tokens and cost, then exit." };
        var offline = new Option<bool>("--offline") { Description = "Serve every model call from evals/cache/; a cache miss fails the document. No API key needed." };
        var evalsDir = new Option<DirectoryInfo>("--evals-dir") { DefaultValueFactory = _ => new("evals") };
        var promptsDir = new Option<DirectoryInfo>("--prompts-dir") { DefaultValueFactory = _ => new("prompts") };

        var command = new Command("run", "Extract and score the eval set with one configuration.") { config, limit, dryRun, offline, evalsDir, promptsDir };
        command.SetAction((r, ct) => RunAsync(r.GetValue(config)!, r.GetValue(limit), r.GetValue(dryRun), r.GetValue(offline), r.GetValue(evalsDir)!.FullName, r.GetValue(promptsDir)!.FullName, ct));
        return command;
    }

    private static async Task<int> RunAsync(string configName, int? limit, bool dryRun, bool offline, string evalsDir, string promptsDir, CancellationToken ct)
    {
        var config = await RunConfig.LoadAsync(Path.Combine(evalsDir, "configs.json"), configName, ct);
        var prompt = await PromptTemplate.LoadAsync(promptsDir, config.Prompt, ct);
        var judge = new NameJudge(await PromptTemplate.LoadAsync(Path.Combine(evalsDir, "judge"), "judge_prompt", ct));
        var docs = (await EvalSet.LoadAsync(evalsDir, config.InputMode, ct)).Take(limit ?? int.MaxValue).ToList();
        var prices = await ModelPrice.LoadAsync(Path.Combine(evalsDir, "pricing.json"), ct);
        var price = prices.GetValueOrDefault(config.Model) ?? throw new InvalidOperationException($"No price for '{config.Model}' in evals/pricing.json.");

        Console.WriteLine($"Config {config.Name}: model {config.Model}, prompt {prompt.Name}@{prompt.Version}, input {config.InputMode}, " +
            $"temperature {config.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "default"}{(config.DisableThinking ? ", thinking off" : "")}");
        Console.WriteLine($"Judge: {judge.Model}, judge_prompt@{judge.PromptVersion}, gray zone only");
        Console.WriteLine($"Documents: {docs.Count} ({docs.Count(d => d.Golden.Source == "fatura")} FATURA, {docs.Count(d => d.Golden.Source == "synthetic")} synthetic)");

        if (dryRun)
        {
            if (config.IsAgent) PrintAgentEstimate(docs, prompt, price, prices.GetValueOrDefault(NameJudge.DefaultModel));
            else PrintEstimate(docs, prompt, price);
            PrintJudgeEstimate(docs, judge, prices.GetValueOrDefault(judge.Model));
            return 0;
        }

        IChatClient network;
        try
        {
            network = offline ? new OfflineChatClient() : AnthropicChatClientFactory.Create(config.Model);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        var provider = new LatencyStampingChatClient(network);
        var gitSha = GitShortSha();
        var executionName = $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-{gitSha}";
        var reporting = new ReportingConfiguration(
            config.IsAgent ? [new CompositeScoreEvaluator(judge), new AgentBehaviorEvaluator(), new AgentQualityEvaluator()] : [new CompositeScoreEvaluator(judge)],
            new DiskBasedResultStore(Path.Combine(evalsDir, "results", "store")),
            new ChatConfiguration(provider),
            new DiskBasedResponseCacheProvider(Path.Combine(evalsDir, "cache"), timeToLiveForCacheEntries: TimeSpan.FromDays(3650)),
            // The cache key ignores ChatOptions.Tools; the fingerprint makes an edited tool definition a cache miss.
            cachingKeys: config.IsAgent ? [InvoiceTools.Fingerprint(InvoiceTools.All)] : null,
            executionName: executionName,
            tags:
            [
                $"{StoredResults.ModelTag}{config.Model}", $"{StoredResults.PromptTag}{prompt.Name}@{prompt.Version}",
                $"{StoredResults.InputTag}{config.InputMode}", $"{StoredResults.JudgeTag}{judge.PromptVersion}",
            ]);
        Console.WriteLine($"Execution: {executionName}{(offline ? " (offline: cache only)" : "")}");

        using var experiment = LangfuseOptions.FromEnvironment(Console.Out) is { } langfuse
            ? LangfuseExperiment.Start(langfuse,
                new ExperimentInfo(executionName, config.Name, config.Model, $"{prompt.Name}@{prompt.Version}", config.InputMode, judge.PromptVersion, gitSha),
                LangfuseItems.Load(Path.Combine(evalsDir, LangfuseItems.FileName)), Console.Out)
            : null;

        var done = 0;
        var failures = new List<string>();
        var misses = new List<string>();
        await Parallel.ForEachAsync(docs, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct }, async (doc, token) =>
        {
            using var trace = experiment?.StartItem(doc, config.InputMode);
            try
            {
                await using var run = await reporting.CreateScenarioRunAsync(
                    doc.ScenarioName, config.Name, additionalTags: [$"{StoredResults.SourceTag}{doc.Golden.Source}", $"{StoredResults.LayoutTag}{doc.Golden.Layout}"], cancellationToken: token);
                var chat = new TracingChatClient(run.ChatConfiguration!.ChatClient);
                IInvoiceExtractor extractor = config.IsAgent ? new AgentInvoiceExtractor(chat, config, prompt) : new ChatInvoiceExtractor(chat, config, prompt);
                var result = await extractor.ExtractAsync(doc, token);
                // FATURA's OCR layer never contains the vendor name, so the judge must not be asked about it in ocr mode.
                var vendorNameInInput = !(config.InputMode == EvalSet.OcrMode && doc.Golden.OcrTextPath is not null);
                var evaluation = await run.EvaluateAsync(result.Messages, result.Response,
                    [new GoldenInvoiceContext(doc.Golden.Expected), new DocumentTextContext(doc.TextLayer, vendorNameInInput)], token);
                if ((CompositeScoreEvaluator.FailureOf(evaluation) ?? CacheMissIn(evaluation)) is { } error)
                {
                    // ScenarioRun swallowed an evaluator exception (name judge or agent quality judge); its text names the exception type.
                    var miss = error.Contains(typeof(CacheMissException).FullName!, StringComparison.Ordinal);
                    var firstLine = error.Split('\n', 2)[0].Trim();
                    trace?.Activity?.SetStatus(ActivityStatusCode.Error, firstLine);
                    lock (miss ? misses : failures) (miss ? misses : failures).Add(miss ? doc.Id : $"{doc.Id}: evaluation failed: {firstLine}");
                    Console.WriteLine($"[{Interlocked.Increment(ref done),3}/{docs.Count}] {doc.Id,-42} {(miss ? "CACHE MISS (evaluator)" : $"EVALUATION FAILED: {firstLine}")}");
                    return;
                }
                if (trace is not null) experiment!.Complete(trace, result, evaluation);
                var composite = evaluation.Get<NumericMetric>(CompositeScoreEvaluator.MetricName).Value;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{Interlocked.Increment(ref done),3}/{docs.Count}] {doc.Id,-42} composite {composite:0.000}  {result.Latency.TotalMilliseconds,6:0} ms{(result.ParseError is null ? "" : "  PARSE ERROR")}"));
            }
            catch (CacheMissException ex)
            {
                trace?.Fail(ex);
                lock (misses) misses.Add(doc.Id);
                Console.WriteLine($"[{Interlocked.Increment(ref done),3}/{docs.Count}] {doc.Id,-42} CACHE MISS");
            }
            catch (Exception ex) when (ex is HttpRequestException or Anthropic.Exceptions.AnthropicApiException or TaskCanceledException && !token.IsCancellationRequested)
            {
                trace?.Fail(ex);
                lock (failures) failures.Add($"{doc.Id}: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[{Interlocked.Increment(ref done),3}/{docs.Count}] {doc.Id,-42} FAILED after {AnthropicChatClientFactory.MaxRetries} retries: {ex.Message}");
            }
        });

        if (experiment is not null) await experiment.FinishAsync(Console.Out, ct);

        var served = docs.Count - failures.Count - misses.Count - (offline ? 0 : provider.NetworkCalls);
        Console.WriteLine($"Network calls: {(offline ? 0 : provider.NetworkCalls)} (extraction + judge), documents fully served from cache: {Math.Max(0, served)}, failed: {failures.Count}, cache misses: {misses.Count}");
        failures.Take(MaxListedFailures).ToList().ForEach(f => Console.WriteLine($"  {f}"));
        if (misses.Count > 0)
        {
            misses.Sort(StringComparer.Ordinal);
            Console.Error.WriteLine(CacheMissMessage(config.Name, misses));
            return 3;
        }
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>An evaluator's swallowed cache miss on any metric (the composite check covers only the deterministic evaluators and the name judge).</summary>
    internal static string? CacheMissIn(EvaluationResult evaluation) =>
        evaluation.Metrics.Values.SelectMany(m => m.Diagnostics ?? [])
            .FirstOrDefault(d => d.Severity == EvaluationDiagnosticSeverity.Error && d.Message.Contains(typeof(CacheMissException).FullName!, StringComparison.Ordinal))?.Message;

    /// <summary>The CI failure text: names the documents and config, and tells the author what to do.</summary>
    internal static string CacheMissMessage(string config, IReadOnlyList<string> docIds) =>
        $"""
        Cache miss (offline) for config '{config}' on {docIds.Count} document(s): {string.Join(", ", docIds.Take(MaxListedFailures))}{(docIds.Count > MaxListedFailures ? ", …" : "")}
        A prompt, model, judge prompt, tool definition, config or input changed, so the committed responses in evals/cache/ no longer apply.
        Run it locally with an API key and commit the cache:
          EVALS_API_KEY=... dotnet run --project src/InvoiceEvals.Cli -- run --config {config}
          git add evals/cache && git commit -m "Refresh response cache for {config}"
        """;

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

    // Agent heuristics: Anthropic's tool-use system prompt plus our tool definitions; a tool-call turn's output and the
    // history it adds (call + result); a quality-judge call's template and output.
    private const int ToolUseOverheadTokens = 350;
    private const int ToolTurnOutputTokens = 120;
    private const int ToolRoundHistoryTokens = 200;
    private const int ExpectedAgentTurns = 3;
    private const int QualityJudgeTemplateTokens = 1200;
    private const int QualityJudgeOutputTokens = 300;

    /// <summary>
    /// Every turn resends the instructions, document, schema and tools, plus the history so far. Expected: 3 turns (one
    /// or two tool rounds, then the answer). Upper bound: <see cref="AgentInvoiceExtractor.MaxToolRounds"/> rounds plus
    /// the forced answer turn. Plus two quality-judge calls per document on the evaluation model.
    /// </summary>
    private static void PrintAgentEstimate(List<EvalDocument> docs, PromptTemplate prompt, ModelPrice price, ModelPrice? judgePrice)
    {
        var tools = InvoiceTools.All.OfType<AIFunctionDeclaration>().Sum(t => t.Name.Length + (t.Description?.Length ?? 0) + t.JsonSchema.GetRawText().Length) / CharsPerToken + ToolUseOverheadTokens;
        (double In, double Out) Turns(int turns) => (
            docs.Sum(d => (turns * (((prompt.Body.Length + d.Text.Length) / CharsPerToken) + SchemaOverheadTokens + tools)) + (ToolRoundHistoryTokens * turns * (turns - 1) / 2.0)) * EstimateMargin,
            docs.Sum(d => ((turns - 1) * ToolTurnOutputTokens) + (JsonSerializer.Serialize(d.Golden.Expected, GoldenSet.JsonOptions).Length / CharsPerToken)) * EstimateMargin);
        var expected = Turns(ExpectedAgentTurns);
        var upper = Turns(AgentInvoiceExtractor.MaxToolRounds + 1);
        var perDoc = 1000.0 / Math.Max(1, docs.Count);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Estimated agent tokens ({ExpectedAgentTurns} turns/doc): {expected.In:N0} input, {expected.Out:N0} output; cost ${price.Cost(expected.In, expected.Out):0.000} (${(double)price.Cost(expected.In, expected.Out) * perDoc:0.00} per 1k documents)"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Upper bound ({AgentInvoiceExtractor.MaxToolRounds} tool rounds + answer on every document): {upper.In:N0} input, {upper.Out:N0} output; cost ${price.Cost(upper.In, upper.Out):0.000}. Thinking tokens, if any, are extra. Cached documents cost nothing."));
        if (judgePrice is null) return;
        var judgeIn = docs.Sum(d => QualityJudgeTemplateTokens + ((prompt.Body.Length + d.Text.Length) / CharsPerToken) + tools + (ExpectedAgentTurns * ToolRoundHistoryTokens)) * 2 * EstimateMargin;
        var judgeOut = docs.Count * 2 * QualityJudgeOutputTokens * EstimateMargin;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Quality judges (tool_call_accuracy + task_adherence, {NameJudge.DefaultModel}): {docs.Count * 2} calls, {judgeIn:N0} input, {judgeOut:N0} output; cost ${judgePrice.Cost(judgeIn, judgeOut):0.000}."));
    }

    /// <summary>Upper bound: both name fields of every document in the gray zone. Real judge calls are a small fraction.</summary>
    private static void PrintJudgeEstimate(List<EvalDocument> docs, NameJudge judge, ModelPrice? price)
    {
        if (price is null)
        {
            Console.WriteLine($"Judge estimate unavailable: no price for '{judge.Model}' in evals/pricing.json.");
            return;
        }
        var calls = docs.Count * FieldAccuracyEvaluator.JudgedFieldNames.Count;
        var perCallInput = (judge.Messages("vendor_name", "", "", "").Sum(m => m.Text.Length) + NameJudge.MaxExcerptChars) / CharsPerToken + SchemaOverheadTokens;
        var cost = price.Cost(calls * perCallInput * EstimateMargin, calls * JudgeOutputTokens * EstimateMargin);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Judge upper bound: {calls} calls if every name were in the gray zone, ~{perCallInput:N0} input tokens each, ${cost:0.000} at most."));
    }

    internal static string GitShortSha()
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
