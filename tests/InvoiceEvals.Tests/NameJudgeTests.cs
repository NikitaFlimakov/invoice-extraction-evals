using System.Text.Json;

using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Tests;

public sealed class NameJudgeTests : IDisposable
{
    private static readonly PromptTemplate Prompt = new("judge_prompt", "1.0", "test", "Judge the names.\n");

    private static readonly InvoiceDto Golden = new(
        new Party("Acme International Ltd", null), new Party("Jane Doe", null),
        "INV-1", null, null, "USD", null, null, null, 10m, null);

    private const string TextLayer = "INVOICE\nAcme International Ltd\n12 Main St\nSpringfield, IL 62701 US\nBill to:\nJane Doe\nTOTAL 10.00 USD\n";

    private readonly string root = Directory.CreateTempSubdirectory("evals-judge-test").FullName;

    // ---------- Parse ----------

    [Theory]
    [InlineData("""{"reason":"Abbreviation of International.","equivalent":true}""", true, "Abbreviation of International.")]
    [InlineData("""{"equivalent":false,"reason":"Different company."}""", false, "Different company.")]
    [InlineData("```json\n{\"reason\":\"r\",\"equivalent\":true}\n```", true, "r")]
    [InlineData("Here you go: {\"reason\":\"r\",\"equivalent\":\"false\"} hope it helps", false, "r")]
    [InlineData("""{"Equivalent":true}""", true, "")]
    public void Parse_AcceptsVerdicts(string text, bool equivalent, string reason)
    {
        var verdict = NameJudge.Parse(text);

        Assert.Equal(equivalent, verdict.Equivalent);
        Assert.Equal(reason, verdict.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yes")]
    [InlineData("""{"reason":"r"}""")]
    [InlineData("""{"reason":"r","equivalent":"maybe"}""")]
    [InlineData("""{"reason":"r","equivalent":1}""")]
    [InlineData("""{"reason":"r","equivalent":tru}""")]
    [InlineData("[true]")]
    public void Parse_RejectsWithoutThrowing(string text)
    {
        var verdict = NameJudge.Parse(text);

        Assert.Null(verdict.Equivalent);
        Assert.StartsWith("unparsed", verdict.Reason, StringComparison.Ordinal);
    }

    // ---------- Excerpt ----------

    [Fact]
    public void Excerpt_IsTheLinesAroundTheAnnotatedName()
    {
        var text = string.Join('\n', Enumerable.Range(0, 20).Select(i => i == 10 ? "Seller: ACME INTERNATIONAL LTD" : $"line {i}"));

        var excerpt = NameJudge.Excerpt(text, "Acme International Ltd", "Acme Intl.");

        Assert.Equal(string.Join('\n', "line 7", "line 8", "line 9", "Seller: ACME INTERNATIONAL LTD", "line 11", "line 12", "line 13"), excerpt);
    }

    [Fact]
    public void Excerpt_FallsBackToNormalizedMatch_ThenToTheTop()
    {
        Assert.Contains("Acme, International", NameJudge.Excerpt("a\nb\nc\nd\ne\nf\ng\nh\nAcme, International\n", "Acme International Ltd.", null), StringComparison.Ordinal);
        Assert.Equal("l0\nl1\nl2\nl3\nl4\nl5\nl6", NameJudge.Excerpt(string.Join('\n', Enumerable.Range(0, 20).Select(i => $"l{i}")), "Nobody", "Nothing"));
    }

    [Fact]
    public void Messages_CarryBothNamesAndTheExcerpt()
    {
        var user = new NameJudge(Prompt).Messages("customer_name", "Jane Doe", "Doe, Jane", TextLayer)[1].Text;

        Assert.Contains("customer (bill-to) name", user, StringComparison.Ordinal);
        Assert.Contains("Annotated name: Jane Doe", user, StringComparison.Ordinal);
        Assert.Contains("Extracted name: Doe, Jane", user, StringComparison.Ordinal);
        Assert.Contains("Bill to:\nJane Doe", user, StringComparison.Ordinal);
    }

    // ---------- judge-assisted FieldAccuracyEvaluator ----------

    [Fact]
    public async Task Judged_GrayZone_AsksJudge_EquivalentScores1_StrictStays0()
    {
        var chat = new FakeJudgeClient(equivalent: true);

        var result = await Evaluate(chat, Golden with { Vendor = new Party("Acme Intl.", null) });

        Assert.Equal(0, Metric(result, FieldAccuracyEvaluator.MetricName("vendor_name")).Value);
        var judged = Metric(result, FieldAccuracyEvaluator.JudgedMetricName("vendor_name"));
        Assert.Equal(1, judged.Value);
        Assert.Equal(FieldAccuracyEvaluator.JudgeOutcome.Equivalent, judged.Metadata![FieldAccuracyEvaluator.JudgeKey]);
        Assert.Equal(FieldAccuracyEvaluator.Outcome.CorrectValue, judged.Metadata[FieldAccuracyEvaluator.OutcomeKey]);
        var call = Assert.Single(chat.Calls);
        Assert.Equal(NameJudge.DefaultModel, call.Options!.ModelId);
        Assert.Equal(0f, call.Options.Temperature);
        Assert.Contains("Extracted name: Acme Intl.", call.Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Judged_NotEquivalent_Scores0_WithJudgeReason()
    {
        var result = await Evaluate(new FakeJudgeClient(equivalent: false), Golden with { Customer = new Party("Acme Logistics", null) });

        var judged = Metric(result, FieldAccuracyEvaluator.JudgedMetricName("customer_name"));
        Assert.Equal(0, judged.Value);
        Assert.Equal(FieldAccuracyEvaluator.JudgeOutcome.NotEquivalent, judged.Metadata![FieldAccuracyEvaluator.JudgeKey]);
        Assert.Contains("fake reason", judged.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Judged_OutsideGrayZone_NeverCallsJudge_AndCopiesStrict()
    {
        var chat = new FakeJudgeClient(equivalent: true);

        // Vendor matches strictly (suffix), customer is a miss (null), so neither is in the gray zone.
        var result = await Evaluate(chat, Golden with { Vendor = new Party("ACME INTERNATIONAL", null), Customer = null });

        Assert.Empty(chat.Calls);
        Assert.Equal(1, Metric(result, FieldAccuracyEvaluator.JudgedMetricName("vendor_name")).Value);
        var customer = Metric(result, FieldAccuracyEvaluator.JudgedMetricName("customer_name"));
        Assert.Equal(0, customer.Value);
        Assert.Equal(FieldAccuracyEvaluator.Outcome.Miss, customer.Metadata![FieldAccuracyEvaluator.OutcomeKey]);
        Assert.Equal(FieldAccuracyEvaluator.JudgeOutcome.NotNeeded, customer.Metadata[FieldAccuracyEvaluator.JudgeKey]);
    }

    [Fact]
    public async Task Judged_VendorNotInModelInput_IsNotAsked_CustomerStillIs()
    {
        var chat = new FakeJudgeClient(equivalent: true);

        var result = await Evaluate(chat, Golden with { Vendor = new Party("SK DIGITAL", null), Customer = new Party("Doe, Jane", null) }, vendorNameInModelInput: false);

        var vendor = Metric(result, FieldAccuracyEvaluator.JudgedMetricName("vendor_name"));
        Assert.Equal(0, vendor.Value);
        Assert.Equal(FieldAccuracyEvaluator.JudgeOutcome.NotInModelInput, vendor.Metadata![FieldAccuracyEvaluator.JudgeKey]);
        Assert.Contains("customer (bill-to) name", Assert.Single(chat.Calls).Messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Judged_UnparsedVerdict_ScoresMismatch_WithWarning()
    {
        var result = await Evaluate(new FakeJudgeClient(raw: "I think so"), Golden with { Vendor = new Party("Acme Intl.", null) });

        var judged = Metric(result, FieldAccuracyEvaluator.JudgedMetricName("vendor_name"));
        Assert.Equal(0, judged.Value);
        Assert.Equal(FieldAccuracyEvaluator.JudgeOutcome.Unparsed, judged.Metadata![FieldAccuracyEvaluator.JudgeKey]);
        Assert.Contains(judged.Diagnostics!, d => d.Severity == EvaluationDiagnosticSeverity.Warning);
    }

    [Fact]
    public async Task Judged_WithoutTextContext_FailsLoudly()
    {
        var evaluator = new FieldAccuracyEvaluator(new NameJudge(Prompt));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await evaluator.EvaluateAsync(
            [], Response(Golden with { Vendor = new Party("Acme Intl.", null) }), new ChatConfiguration(new FakeJudgeClient(true)),
            [new GoldenInvoiceContext(Golden)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Composite_IgnoresJudgedMetrics()
    {
        var predicted = Golden with { Vendor = new Party("Acme Intl.", null) };
        var plain = await new CompositeScoreEvaluator().EvaluateAsync([], Response(predicted), additionalContext: [new GoldenInvoiceContext(Golden)], cancellationToken: TestContext.Current.CancellationToken);
        var judged = await new CompositeScoreEvaluator(new NameJudge(Prompt)).EvaluateAsync([], Response(predicted), new ChatConfiguration(new FakeJudgeClient(true)),
            [new GoldenInvoiceContext(Golden), new DocumentTextContext(TextLayer, true)], TestContext.Current.CancellationToken);

        Assert.Equal(Metric(plain, CompositeScoreEvaluator.MetricName).Value, Metric(judged, CompositeScoreEvaluator.MetricName).Value);
        Assert.Equal(1, Metric(judged, FieldAccuracyEvaluator.JudgedMetricName("vendor_name")).Value);
    }

    // ---------- Reporting cache and offline replay ----------

    [Fact]
    public async Task JudgeCalls_AreCachedByReporting_AndReplayOffline()
    {
        var predicted = Golden with { Vendor = new Party("Acme Intl.", null) };
        var live = new FakeJudgeClient(equivalent: true);

        var first = await ScenarioAsync(live, predicted);
        var second = await ScenarioAsync(live, predicted);
        var offline = await ScenarioAsync(new OfflineChatClient(), predicted);

        Assert.Single(live.Calls);
        Assert.All([first, second, offline], r => Assert.Equal(1, Metric(r, FieldAccuracyEvaluator.JudgedMetricName("vendor_name")).Value));
    }

    // ScenarioRun swallows evaluator exceptions into null metrics; FailureOf must surface the cache miss.
    [Fact]
    public async Task Offline_UncachedJudgeCall_IsReportedAsCacheMissFailure()
    {
        var result = await ScenarioAsync(new OfflineChatClient(), Golden with { Vendor = new Party("Acme Intl.", null) });

        var failure = CompositeScoreEvaluator.FailureOf(result);
        Assert.NotNull(failure);
        Assert.StartsWith(typeof(CacheMissException).FullName!, failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_NoJudgeNeeded_Succeeds()
    {
        var result = await ScenarioAsync(new OfflineChatClient(), Golden);

        Assert.Null(CompositeScoreEvaluator.FailureOf(result));
        Assert.Equal(1, Metric(result, CompositeScoreEvaluator.MetricName).Value);
    }

    private async Task<EvaluationResult> ScenarioAsync(IChatClient provider, InvoiceDto predicted)
    {
        var ct = TestContext.Current.CancellationToken;
        var reporting = new ReportingConfiguration(
            [new CompositeScoreEvaluator(new NameJudge(Prompt))],
            new DiskBasedResultStore(Path.Combine(root, "store")),
            new ChatConfiguration(new LatencyStampingChatClient(provider)),
            new DiskBasedResponseCacheProvider(Path.Combine(root, "cache"), TimeSpan.FromDays(3650)),
            executionName: $"exec-{Guid.NewGuid():N}");
        await using var run = await reporting.CreateScenarioRunAsync("fatura.T1_I1", "mini-plain", cancellationToken: ct);
        return await run.EvaluateAsync([], Response(predicted), [new GoldenInvoiceContext(Golden), new DocumentTextContext(TextLayer, true)], ct);
    }

    // ---------- helpers ----------

    private static async Task<EvaluationResult> Evaluate(IChatClient chat, InvoiceDto predicted, bool vendorNameInModelInput = true) =>
        await new FieldAccuracyEvaluator(new NameJudge(Prompt)).EvaluateAsync(
            [], Response(predicted), new ChatConfiguration(chat),
            [new GoldenInvoiceContext(Golden), new DocumentTextContext(TextLayer, vendorNameInModelInput)],
            TestContext.Current.CancellationToken);

    private static ChatResponse Response(InvoiceDto predicted) =>
        new(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(predicted, GoldenSet.JsonOptions)));

    private static NumericMetric Metric(EvaluationResult result, string name) => result.Get<NumericMetric>(name);

    public void Dispose() => Directory.Delete(root, recursive: true);

    internal sealed class FakeJudgeClient(bool equivalent = true, string? raw = null) : IChatClient
    {
        private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> calls = [];

        public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls
        {
            get
            {
                lock (calls) return [.. calls];
            }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (calls) calls.Add(([.. messages], options));
            var text = raw ?? JsonSerializer.Serialize(new { reason = "fake reason", equivalent });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                ModelId = options?.ModelId,
                Usage = new UsageDetails { InputTokenCount = 300, OutputTokenCount = 20 },
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
