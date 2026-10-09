using System.Text.Json;

using InvoiceEvals.Agent;
using InvoiceEvals.Cli;
using InvoiceEvals.Core;
using InvoiceEvals.Evaluation;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace InvoiceEvals.Tests;

public sealed class AgentEvaluatorTests
{
    // Printed amounts do not reconcile (100 + 5 ≠ 120), like FATURA.
    private static readonly InvoiceDto Golden = new(null, null, "INV-1", null, null, "USD", 100.00m, null, 5.00m, 120.00m, null);
    private const string Text = "INVOICE INV-1\nSUBTOTAL $100.00\nTAX $5.00\nTOTAL $120.00\n";

    [Fact]
    public async Task ToolCounts_AndCalledRates_ComeFromTheTranscript()
    {
        var response = Transcript(Answer(120.00m), Validate(100.00m, 5.00m, 120.00m), Currency("$"));

        var result = await Evaluate(response);

        Assert.Equal(2, Value(result, AgentBehaviorEvaluator.ToolCallCount));
        Assert.Equal(1, Value(result, AgentBehaviorEvaluator.ValidateTotalsCalled));
        Assert.Equal(1, Value(result, AgentBehaviorEvaluator.NormalizeCurrencyCalledSymbol));
        Assert.Null(Value(result, AgentBehaviorEvaluator.NormalizeCurrencyCalledCode));
    }

    [Fact]
    public async Task NoToolCalls_ScoresZeroCalledRates()
    {
        var result = await Evaluate(Transcript(Answer(120.00m)));

        Assert.Equal(0, Value(result, AgentBehaviorEvaluator.ToolCallCount));
        Assert.Equal(0, Value(result, AgentBehaviorEvaluator.ValidateTotalsCalled));
        Assert.Equal(0, Value(result, AgentBehaviorEvaluator.NormalizeCurrencyCalledSymbol));
        Assert.Null(Value(result, AgentBehaviorEvaluator.ToolOverride));
    }

    [Fact]
    public async Task TextWithIsoCode_CountsUnderTheCodeSplit()
    {
        var result = await Evaluate(Transcript(Answer(120.00m), Currency("USD")), text: "TOTAL 120.00 USD\n");

        Assert.Null(Value(result, AgentBehaviorEvaluator.NormalizeCurrencyCalledSymbol));
        Assert.Equal(1, Value(result, AgentBehaviorEvaluator.NormalizeCurrencyCalledCode));
    }

    [Fact]
    public async Task TotalReplacedByTheReconciledValue_IsAToolOverride()
    {
        var result = await Evaluate(Transcript(Answer(105.00m), Validate(100.00m, 5.00m, 120.00m)));

        Assert.Equal(1, Value(result, AgentBehaviorEvaluator.ToolOverride));
    }

    [Fact]
    public async Task PrintedTotalKept_AfterAnInconsistency_IsNotAnOverride()
    {
        var result = await Evaluate(Transcript(Answer(120.00m), Validate(100.00m, 5.00m, 120.00m)));

        Assert.Equal(0, Value(result, AgentBehaviorEvaluator.ToolOverride));
    }

    [Fact]
    public async Task ToolOverride_IsNotApplicable_WhenTheToolFoundNoInconsistency()
    {
        var result = await Evaluate(Transcript(Answer(105.00m), Validate(100.00m, 5.00m, 105.00m)));

        Assert.Null(Value(result, AgentBehaviorEvaluator.ToolOverride));
    }

    [Fact]
    public async Task TotalsWarning_IsCorrect_WhenGoldenAmountsDoNotReconcile()
    {
        var result = await Evaluate(Transcript(Answer(120.00m, "totals_inconsistent: difference 15.00")));

        Assert.Equal(1, Value(result, AgentBehaviorEvaluator.TotalsWarningCorrect));
    }

    [Fact]
    public async Task TotalsWarning_IsWrong_WhenGoldenAmountsReconcile()
    {
        var reconciling = Golden with { Total = 105.00m };

        var result = await Evaluate(Transcript(Answer(105.00m, "TOTALS_INCONSISTENT: misread tax")), reconciling);

        Assert.Equal(0, Value(result, AgentBehaviorEvaluator.TotalsWarningCorrect));
    }

    [Fact]
    public async Task OtherWarnings_DoNotCountForPrecision()
    {
        var result = await Evaluate(Transcript(Answer(120.00m, "currency ambiguous")));

        Assert.Null(Value(result, AgentBehaviorEvaluator.TotalsWarningCorrect));
    }

    [Fact]
    public async Task DeterministicEvaluators_ScoreTheFinalAnswer_NotTheToolTurns()
    {
        var response = Transcript(Answer(120.00m), Validate(100.00m, 5.00m, 120.00m));
        response.Messages.Insert(0, new ChatMessage(ChatRole.Assistant, "Let me check the totals first."));

        var result = await new CompositeScoreEvaluator().EvaluateAsync([], response, additionalContext: [new GoldenInvoiceContext(Golden)], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Get<NumericMetric>(SchemaValidityEvaluator.MetricName).Value);
        Assert.Equal(1, result.Get<NumericMetric>(FieldAccuracyEvaluator.MetricName("total")).Value);
    }

    [Fact]
    public async Task QualityEvaluator_PinsTheEvaluationModel_AndCallsBothJudges()
    {
        var judge = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "not in the expected format")));
        var response = Transcript(Answer(120.00m), Validate(100.00m, 5.00m, 120.00m));

        var result = await new AgentQualityEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.System, "Extract."), new ChatMessage(ChatRole.User, Text)], response, new ChatConfiguration(judge),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, judge.Calls);
        Assert.All(judge.Options, o => Assert.Equal(NameJudge.DefaultModel, o!.ModelId));
        Assert.Contains(AgentQualityEvaluator.ToolCallAccuracy, result.Metrics.Keys);
        Assert.Contains(AgentQualityEvaluator.TaskAdherence, result.Metrics.Keys);
    }

    [Fact]
    public async Task QualityEvaluator_CacheMissOffline_IsDetected()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("evals-quality-test").FullName;
        try
        {
            var reporting = new ReportingConfiguration(
                [new AgentQualityEvaluator()], new DiskBasedResultStore(Path.Combine(root, "store")),
                new ChatConfiguration(new LatencyStampingChatClient(new OfflineChatClient())),
                new DiskBasedResponseCacheProvider(Path.Combine(root, "cache"), TimeSpan.FromDays(3650)), executionName: "exec");
            await using var run = await reporting.CreateScenarioRunAsync("fatura.T1_I1", "agent-mini", cancellationToken: ct);

            var evaluation = await run.EvaluateAsync([new ChatMessage(ChatRole.User, Text)], Transcript(Answer(120.00m), Validate(100.00m, 5.00m, 120.00m)), [], ct);

            Assert.NotNull(RunCommand.CacheMissIn(evaluation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<EvaluationResult> Evaluate(ChatResponse response, InvoiceDto? golden = null, string text = Text) =>
        new AgentBehaviorEvaluator().EvaluateAsync([], response,
            additionalContext: [new GoldenInvoiceContext(golden ?? Golden), new DocumentTextContext(text, vendorNameInModelInput: true)],
            cancellationToken: TestContext.Current.CancellationToken).AsTask();

    private static double? Value(EvaluationResult result, string metric) => result.Get<NumericMetric>(metric).Value;

    private static string Answer(decimal total, params string[] warnings) =>
        JsonSerializer.Serialize(new { invoiceNumber = "INV-1", currency = "USD", subtotal = 100.00m, tax = 5.00m, total, warnings });

    private static (FunctionCallContent, FunctionResultContent) Validate(decimal subtotal, decimal tax, decimal total)
    {
        var id = $"v{subtotal}{tax}{total}";
        return (new FunctionCallContent(id, InvoiceTools.ValidateTotalsName, new Dictionary<string, object?> { ["subtotal"] = subtotal, ["tax"] = tax, ["total"] = total }),
            new FunctionResultContent(id, JsonSerializer.SerializeToElement(InvoiceTools.ValidateTotals(subtotal, null, tax, total), AIJsonUtilities.DefaultOptions)));
    }

    private static (FunctionCallContent, FunctionResultContent) Currency(string text) =>
        (new FunctionCallContent($"c{text}", InvoiceTools.NormalizeCurrencyName, new Dictionary<string, object?> { ["text"] = text }),
            new FunctionResultContent($"c{text}", JsonSerializer.SerializeToElement(InvoiceTools.NormalizeCurrency(text), AIJsonUtilities.DefaultOptions)));

    /// <summary>One tool round with all <paramref name="tools"/>, then the answer; tool-free when none are given.</summary>
    private static ChatResponse Transcript(string answer, params (FunctionCallContent Call, FunctionResultContent Result)[] tools)
    {
        List<ChatMessage> messages = tools.Length == 0
            ? []
            : [new(ChatRole.Assistant, [.. tools.Select(t => (AIContent)t.Call)]), new(ChatRole.Tool, [.. tools.Select(t => (AIContent)t.Result)])];
        messages.Add(new ChatMessage(ChatRole.Assistant, answer));
        return new ChatResponse(messages);
    }
}
