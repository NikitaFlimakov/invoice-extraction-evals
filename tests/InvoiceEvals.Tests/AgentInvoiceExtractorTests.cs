using System.Diagnostics;

using InvoiceEvals.Agent;
using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Tests;

/// <summary>The agent loop against a scripted chat client: answer shape, round limit, response cache and spans.</summary>
public sealed class AgentInvoiceExtractorTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("evals-agent-test").FullName;

    private static readonly PromptTemplate Prompt = new("agent", "1.0", "test", "Extract the invoice.\n");
    private static readonly RunConfig Config = new("agent-mini", "claude-haiku-4-5", "agent", "text", 0, Extractor: RunConfig.Agent);
    private static readonly EvalDocument Doc = new(
        new GoldenDocument("fatura/T1_I1", "fatura", "T1", "golden/fatura/T1_I1.jpg", "golden/fatura/T1_I1.txt", null,
            new InvoiceDto(null, null, "INV-1", null, null, "USD", 100m, null, 5m, 110m, null)),
        "INVOICE INV-1\nSUBTOTAL $100.00\nTAX $5.00\nTOTAL $110.00\n");

    private const string Answer = """{"invoiceNumber":"INV-1","currency":"USD","subtotal":100.00,"tax":5.00,"total":110.00,"warnings":["totals_inconsistent: difference 5.00"]}""";

    [Fact]
    public async Task ToolCallsThenAnswer_ReturnsInvoiceWarningsAndSummedUsage()
    {
        var model = ScriptedChatClient.ToolsThenAnswer(Answer);

        var result = await new AgentInvoiceExtractor(new LatencyStampingChatClient(model), Config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Calls);
        Assert.Equal(110.00m, result.Invoice!.Total);
        Assert.Equal(["totals_inconsistent: difference 5.00"], result.Warnings);
        Assert.Equal(200, result.InputTokens);
        Assert.Equal(Answer, result.RawText);
        var results = result.Response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        Assert.Equal(2, results.Count);
        Assert.Contains("\"consistent\":false", System.Text.Json.JsonSerializer.Serialize(results[0].Result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecondRequest_CarriesToolsInstructionsAndTheAnswerSchema()
    {
        var model = ScriptedChatClient.ToolsThenAnswer(Answer);

        await new AgentInvoiceExtractor(model, Config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        var options = model.Options[1]!;
        Assert.Equal("claude-haiku-4-5", options.ModelId);
        Assert.Equal(0f, options.Temperature);
        Assert.Equal("Extract the invoice.\n", options.Instructions);
        Assert.Equal([InvoiceTools.ValidateTotalsName, InvoiceTools.NormalizeCurrencyName], options.Tools!.Select(t => t.Name));
        var schema = Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat).Schema!.Value;
        Assert.True(schema.GetProperty("properties").TryGetProperty("warnings", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("invoiceNumber", out _));
    }

    [Fact]
    public async Task ModelThatNeverStopsCallingTools_GetsSixRoundsThenAToolFreeAnswerTurn_WithAWarning()
    {
        var model = ScriptedChatClient.AlwaysTools();

        var result = await new AgentInvoiceExtractor(model, Config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        Assert.Equal(AgentInvoiceExtractor.MaxToolRounds + 1, model.Calls);
        Assert.Equal(AgentInvoiceExtractor.MaxToolRounds, result.Response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count());
        var forced = model.Options[^1]!;
        Assert.Equal(ChatToolMode.None, forced.ToolMode);
        Assert.Equal(2, forced.Tools!.Count);
        Assert.Null(result.Invoice);
        Assert.Contains(result.Warnings, w => w.StartsWith(AgentInvoiceExtractor.RoundLimitWarning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CachedReplay_MakesNoModelCalls()
    {
        var model = ScriptedChatClient.ToolsThenAnswer(Answer);
        var first = await RunThroughCacheAsync(new LatencyStampingChatClient(model));
        Assert.Equal(2, model.Calls);

        var second = await RunThroughCacheAsync(new LatencyStampingChatClient(new OfflineChatClient()));

        Assert.Equal(2, model.Calls);
        Assert.Equal(first.RawText, second.RawText);
        Assert.Equal(first.Latency, second.Latency);
    }

    [Fact]
    public async Task Spans_NestUnderTheDocumentRoot_WithToolSpansInsideTheAgentSpan()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == EvalsTelemetry.SourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (spans) spans.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        Activity.Current = null;
        ActivityTraceId traceId;
        using (var rootSpan = EvalsTelemetry.Source.StartActivity("fatura/T1_I1")!)
        {
            traceId = rootSpan.TraceId;
            var chat = new TracingChatClient(new LatencyStampingChatClient(ScriptedChatClient.ToolsThenAnswer(Answer)));
            await new AgentInvoiceExtractor(chat, Config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);
        }

        // Other tests run in parallel on the same ActivitySource and keep adding spans; snapshot this trace only.
        List<Activity> mine;
        lock (spans) mine = [.. spans.Where(s => s.TraceId == traceId)];
        var root = Assert.Single(mine, s => s.Parent is null);
        var agent = Assert.Single(mine, s => s.DisplayName.StartsWith("invoke_agent", StringComparison.Ordinal));
        Assert.Equal(root.SpanId, agent.ParentSpanId);
        var tools = mine.Where(s => s.DisplayName.StartsWith("execute_tool", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, tools.Count);
        Assert.All(tools, t => Assert.True(IsDescendant(t, agent), $"{t.DisplayName} is not under {agent.DisplayName}"));
        Assert.Equal(2, mine.Count(s => s.DisplayName.StartsWith("chat ", StringComparison.Ordinal)));
    }

    private static bool IsDescendant(Activity span, Activity ancestor)
    {
        for (var p = span.Parent; p is not null; p = p.Parent)
        {
            if (p.SpanId == ancestor.SpanId) return true;
        }
        return false;
    }

    private async Task<ExtractionResult> RunThroughCacheAsync(IChatClient provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var reporting = new ReportingConfiguration(
            [new CompositeScoreEvaluator()],
            new DiskBasedResultStore(Path.Combine(root, "store")),
            new ChatConfiguration(provider),
            new DiskBasedResponseCacheProvider(Path.Combine(root, "cache"), TimeSpan.FromDays(3650)),
            executionName: $"exec-{Guid.NewGuid():N}");
        await using var run = await reporting.CreateScenarioRunAsync(Doc.ScenarioName, Config.Name, cancellationToken: ct);
        var result = await new AgentInvoiceExtractor(run.ChatConfiguration!.ChatClient, Config, Prompt).ExtractAsync(Doc, ct);
        var evaluation = await run.EvaluateAsync(result.Messages, result.Response, [new GoldenInvoiceContext(Doc.Golden.Expected)], ct);
        Assert.Null(CompositeScoreEvaluator.FailureOf(evaluation));
        return result;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

/// <summary>Replies from a script: each call gets the next response factory; records the options of every call.</summary>
internal sealed class ScriptedChatClient(Func<int, ChatResponse> script) : IChatClient
{
    public int Calls { get; private set; }

    public List<ChatOptions?> Options { get; } = [];

    public static ScriptedChatClient ToolsThenAnswer(string answer) => new(call => call == 0
        ? Turn(new FunctionCallContent("call_1", InvoiceTools.ValidateTotalsName, new Dictionary<string, object?> { ["subtotal"] = 100.00m, ["tax"] = 5.00m, ["total"] = 110.00m }),
               new FunctionCallContent("call_2", InvoiceTools.NormalizeCurrencyName, new Dictionary<string, object?> { ["text"] = "$" }))
        : Turn(new TextContent(answer)));

    public static ScriptedChatClient AlwaysTools() => new(call =>
        Turn(new FunctionCallContent($"call_{call}", InvoiceTools.NormalizeCurrencyName, new Dictionary<string, object?> { ["text"] = "$" })));

    private static ChatResponse Turn(params AIContent[] contents) =>
        new(new ChatMessage(ChatRole.Assistant, [.. contents])) { ModelId = "claude-test", Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 10 } };

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Options.Add(options?.Clone());
        await Task.Delay(10, cancellationToken);
        return script(Calls++);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
