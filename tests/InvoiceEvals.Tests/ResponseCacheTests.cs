using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Tests;

/// <summary>
/// The extractor takes its IChatClient from ScenarioRun.ChatConfiguration, so the Reporting response cache applies.
/// Also shows Microsoft.Extensions.AI.Evaluation.Reporting running under xunit.v3 on Microsoft.Testing.Platform.
/// </summary>
public sealed class ResponseCacheTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("evals-cache-test").FullName;
    private readonly CountingChatClient provider = new();

    private static readonly EvalDocument Doc = new(
        new GoldenDocument("fatura/T1_I1", "fatura", "T1", "golden/fatura/T1_I1.jpg", "golden/fatura/T1_I1.txt", null,
            new InvoiceDto(null, null, "INV-1", null, null, null, null, null, null, 10m, null)),
        "INVOICE INV-1\nTOTAL 10.00 USD\n");

    [Fact]
    public async Task SameRequest_IsServedFromCache_WithOriginalLatency()
    {
        var config = new RunConfig("mini-plain", "claude-haiku-4-5", "plain", "text", 0);
        WritePrompt("Extract the invoice.");

        var first = await RunAsync(config);
        var second = await RunAsync(config);

        Assert.Equal(1, provider.Calls);
        Assert.Equal(first.Latency, second.Latency);
        Assert.Equal(first.RawText, second.RawText);
    }

    [Fact]
    public async Task EditedPrompt_IsACacheMiss()
    {
        var config = new RunConfig("mini-plain", "claude-haiku-4-5", "plain", "text", 0);
        WritePrompt("Extract the invoice.");
        await RunAsync(config);

        WritePrompt("Extract the invoice. Return null for fields that are not printed.");
        await RunAsync(config);

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task ChangedModel_IsACacheMiss()
    {
        WritePrompt("Extract the invoice.");
        await RunAsync(new RunConfig("cfg", "claude-haiku-4-5", "plain", "text", 0));
        await RunAsync(new RunConfig("cfg", "claude-sonnet-5-5", "plain", "text", null));

        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Results_AreStored_UnderDocumentIdAndConfigName()
    {
        WritePrompt("Extract the invoice.");
        await RunAsync(new RunConfig("mini-plain", "claude-haiku-4-5", "plain", "text", 0));

        var results = new List<ScenarioRunResult>();
        await foreach (var r in new DiskBasedResultStore(Path.Combine(root, "store")).ReadResultsAsync(cancellationToken: TestContext.Current.CancellationToken))
            results.Add(r);

        var result = Assert.Single(results);
        Assert.Equal("fatura.T1_I1", result.ScenarioName);
        Assert.Equal("mini-plain", result.IterationName);
        Assert.Equal(1, result.EvaluationResult.Get<NumericMetric>(FieldAccuracyEvaluator.MetricName("total")).Value);
    }

    private async Task<ExtractionResult> RunAsync(RunConfig config)
    {
        var ct = TestContext.Current.CancellationToken;
        var prompt = await PromptTemplate.LoadAsync(root, "plain", ct);
        var reporting = new ReportingConfiguration(
            [new CompositeScoreEvaluator()],
            new DiskBasedResultStore(Path.Combine(root, "store")),
            new ChatConfiguration(new LatencyStampingChatClient(provider)),
            new DiskBasedResponseCacheProvider(Path.Combine(root, "cache"), TimeSpan.FromDays(3650)),
            executionName: $"exec-{Guid.NewGuid():N}");

        await using var run = await reporting.CreateScenarioRunAsync(Doc.ScenarioName, config.Name, cancellationToken: ct);
        var result = await new ChatInvoiceExtractor(run.ChatConfiguration!.ChatClient, config, prompt).ExtractAsync(Doc, ct);
        await run.EvaluateAsync(result.Messages, result.Response, [new GoldenInvoiceContext(Doc.Golden.Expected)], ct);
        return result;
    }

    private void WritePrompt(string body) => File.WriteAllText(Path.Combine(root, "plain.md"), $"---\nversion: 1.0\ndescription: test\n---\n{body}\n");

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(20, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"invoiceNumber":"INV-1","total":10.00}"""))
            {
                ModelId = options?.ModelId,
                Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 },
            };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
