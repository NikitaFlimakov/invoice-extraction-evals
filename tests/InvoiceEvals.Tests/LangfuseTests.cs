using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using InvoiceEvals.Cli.Langfuse;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Tests;

public sealed class LangfuseTests
{
    private static readonly LangfuseOptions Options = new(new Uri("https://langfuse.test/"), "pk-lf-test", "sk-lf-test");

    [Fact]
    public async Task Client_RetriesTransientFailures_ThenSucceeds()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => throw new HttpRequestException("connection reset"),
            _ => Json(HttpStatusCode.OK, """{"id":"item-1"}"""));
        var delays = new List<TimeSpan>();
        using var client = new LangfuseClient(Options, handler, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var id = await client.UpsertDatasetItemAsync(new DatasetItem("item-1", "ds", null, null, null), TestContext.Current.CancellationToken);

        Assert.Equal("item-1", id);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
        Assert.Equal("Basic", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task Client_GivesUpAfterMaxAttempts_WithStatusInMessage()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = new LangfuseClient(Options, handler, (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<LangfuseException>(() => client.CreateDatasetAsync("ds", "d", new { }, TestContext.Current.CancellationToken));

        Assert.Equal(LangfuseClient.MaxAttempts, handler.Requests.Count);
        Assert.Contains("429", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_ClientErrors_AreNotRetried_404DatasetIsNull()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new LangfuseClient(Options, handler, (_, _) => Task.CompletedTask);

        Assert.Null(await client.GetDatasetIdAsync("invoice-extraction-evals", TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Scores_AreBatched_And207IsReportedNotRetried()
    {
        var handler = new ScriptedHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Accepted),
            _ => Json(HttpStatusCode.MultiStatus, """{"accepted":49,"rejected":1,"errors":[{"message":"bad value"}]}"""));
        using var client = new LangfuseClient(Options, handler, (_, _) => Task.CompletedTask);
        var scores = Enumerable.Range(0, LangfuseClient.ScoreBatchSize + 50)
            .Select(i => new Score($"t-{i}", "t", "s", $"m{i}", 1, null, LangfuseOptions.Environment)).ToList();

        var result = await client.CreateScoresAsync(scores, TestContext.Current.CancellationToken);

        Assert.Equal((LangfuseClient.ScoreBatchSize + 49, 1), (result.Accepted, result.Rejected));
        Assert.Equal(["bad value"], result.Errors);
        Assert.Equal(2, handler.Requests.Count);
        using var first = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(LangfuseClient.ScoreBatchSize, first.RootElement.GetArrayLength());
        Assert.Equal("NUMERIC", first.RootElement[0].GetProperty("dataType").GetString());
        Assert.Equal("s", first.RootElement[0].GetProperty("observationId").GetString());
    }

    [Fact]
    public void Options_RequireAllThreeVariables_AndNeverPrintTheSecret()
    {
        Assert.DoesNotContain("sk-lf-test", Options.ToString(), StringComparison.Ordinal);
        Assert.Equal(new Uri("https://langfuse.test/api/public/otel/v1/traces"), Options.OtlpTracesEndpoint);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("pk-lf-test:sk-lf-test")), Options.BasicAuth);
    }

    [Fact]
    public void ItemId_IsStablePerDocument()
    {
        Assert.Equal(LangfuseItems.ItemId("fatura/T1_I1"), LangfuseItems.ItemId("fatura/T1_I1"));
        Assert.NotEqual(LangfuseItems.ItemId("fatura/T1_I1"), LangfuseItems.ItemId("fatura/T1_I2"));
        Assert.StartsWith("iee-", LangfuseItems.ItemId("x"), StringComparison.Ordinal);
    }

    // A cached replay must still produce a model-call span, marked as a cache hit, lasting the original latency.
    [Fact]
    public async Task CachedReplay_EmitsGenAiSpan_WithOriginalLatency()
    {
        var root = Directory.CreateTempSubdirectory("evals-trace-test").FullName;
        var model = $"trace-test-{Guid.NewGuid():N}"; // The listener is process-wide; keep only this test's spans.
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == EvalsTelemetry.SourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (model.Equals(a.GetTagItem("gen_ai.request.model"))) lock (spans) spans.Add(a);
            },
        };
        ActivitySource.AddActivityListener(listener);
        try
        {
            var provider = new SlowClient(TimeSpan.FromMilliseconds(150));
            await CallAsync(root, provider, model);
            await CallAsync(root, provider, model);

            Assert.Equal(1, provider.Calls);
            Assert.Equal(2, spans.Count);
            var (live, replay) = (spans[0], spans[1]);
            Assert.Equal(false, live.GetTagItem(EvalsTelemetry.CacheHitTag));
            Assert.Equal(true, replay.GetTagItem(EvalsTelemetry.CacheHitTag));
            Assert.Equal(live.Duration, replay.Duration);
            // The original ~150 ms call, not the sub-millisecond cache lookup. (Task.Delay may end slightly early on Windows timers.)
            Assert.True(replay.Duration >= TimeSpan.FromMilliseconds(100), $"replay span lasted {replay.Duration}");
            Assert.Equal(1234L, replay.GetTagItem("gen_ai.usage.input_tokens"));
            Assert.Equal(model, replay.GetTagItem("gen_ai.request.model"));
            Assert.Equal("{}", replay.GetTagItem("langfuse.observation.output"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CallAsync(string root, IChatClient provider, string model)
    {
        var ct = TestContext.Current.CancellationToken;
        var reporting = new ReportingConfiguration([], new DiskBasedResultStore(Path.Combine(root, "store")),
            new ChatConfiguration(new LatencyStampingChatClient(provider)),
            new DiskBasedResponseCacheProvider(Path.Combine(root, "cache"), TimeSpan.FromDays(1)), executionName: $"e-{Guid.NewGuid():N}");
        await using var run = await reporting.CreateScenarioRunAsync("s", "i", cancellationToken: ct);
        await new TracingChatClient(run.ChatConfiguration!.ChatClient)
            .GetResponseAsync([new(ChatRole.User, "hi")], new ChatOptions { ModelId = model }, ct);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class SlowClient(TimeSpan latency) : IChatClient
    {
        public int Calls { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Delay(latency, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")) { ModelId = options?.ModelId, Usage = new UsageDetails { InputTokenCount = 1234, OutputTokenCount = 5 } };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
    {
        public List<(string? Authorization, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Headers.Authorization?.Scheme, body));
            var response = script[Math.Min(Requests.Count, script.Length) - 1](request);
            response.RequestMessage = request;
            return response;
        }
    }
}
