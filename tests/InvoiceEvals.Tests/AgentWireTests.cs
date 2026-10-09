using System.Net;
using System.Text;
using System.Text.Json;

using Anthropic;

using InvoiceEvals.Agent;
using InvoiceEvals.Core;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Tests;

/// <summary>The agent through the real Anthropic IChatClient, against a fake HTTP handler that replays scripted messages.</summary>
public sealed class AgentWireTests
{
    private static readonly PromptTemplate Prompt = new("agent", "1.0", "test", "Extract the invoice.\n");
    private static readonly EvalDocument Doc = new(
        new GoldenDocument("fatura/X", "fatura", "T1", "golden/fatura/X.jpg", "golden/fatura/X.txt", null,
            new InvoiceDto(null, null, "INV-1", null, null, "USD", 100m, null, 5m, 110m, null)),
        "INVOICE INV-1\nSUBTOTAL $100.00\nTAX $5.00\nTOTAL $110.00\n");

    private static readonly object ToolUse = new
    {
        type = "tool_use", id = "toolu_1", name = InvoiceTools.ValidateTotalsName,
        input = new { subtotal = 100.00, tax = 5.00, total = 110.00 },
    };

    [Fact]
    public async Task AgentMini_SendsToolsWithTheJsonSchema_ThenReturnsTheToolResult()
    {
        var handler = new ScriptedHandler(
            [ToolUse],
            [new { type = "text", text = """{"invoiceNumber":"INV-1","total":110.00,"warnings":["totals_inconsistent: 5.00"]}""" }]);
        var config = new RunConfig("agent-mini", "claude-haiku-4-5", "agent", "text", 0, Extractor: RunConfig.Agent);

        var result = await new AgentInvoiceExtractor(Client(handler), config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Bodies.Count);
        var first = handler.Bodies[0];
        Assert.Equal([InvoiceTools.ValidateTotalsName, InvoiceTools.NormalizeCurrencyName], first.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal("json_schema", first.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(0, first.GetProperty("temperature").GetDouble());
        Assert.Contains("Extract the invoice.", first.GetProperty("system").GetRawText(), StringComparison.Ordinal);
        var toolResult = handler.Bodies[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray().Single();
        Assert.Equal("tool_result", toolResult.GetProperty("type").GetString());
        Assert.Equal("toolu_1", toolResult.GetProperty("tool_use_id").GetString());
        Assert.Contains("consistent", toolResult.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(110.00m, result.Invoice!.Total);
        Assert.Equal(["totals_inconsistent: 5.00"], result.Warnings);
        Assert.Equal(2 * 1000, result.InputTokens);
    }

    [Fact]
    public async Task AgentStrong_SendsBetweenToolsThinking_AndNoTemperature_OnEveryTurn()
    {
        var handler = new ScriptedHandler([ToolUse], [new { type = "text", text = """{"total":110.00,"warnings":[]}""" }]);
        var config = new RunConfig("agent-strong", "claude-sonnet-5-5", "agent", "text", null, DisableThinking: true, Extractor: RunConfig.Agent);

        await new AgentInvoiceExtractor(Client(handler), config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        Assert.All(handler.Bodies, b =>
        {
            Assert.Equal("between_tools", b.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(b.TryGetProperty("temperature", out _));
            Assert.True(b.TryGetProperty("tools", out _));
        });
    }

    [Fact]
    public async Task ForcedAnswerTurn_KeepsTheToolsWithToolChoiceNone()
    {
        var handler = new ScriptedHandler([.. Enumerable.Repeat<object[]>([ToolUse], AgentInvoiceExtractor.MaxToolRounds), [new { type = "text", text = """{"total":110.00,"warnings":[]}""" }]]);
        var config = new RunConfig("agent-mini", "claude-haiku-4-5", "agent", "text", 0, Extractor: RunConfig.Agent);

        var result = await new AgentInvoiceExtractor(Client(handler), config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        Assert.Equal(AgentInvoiceExtractor.MaxToolRounds + 1, handler.Bodies.Count);
        var last = handler.Bodies[^1];
        Assert.True(last.TryGetProperty("tools", out _));
        Assert.Equal("none", last.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal(110.00m, result.Invoice!.Total);
        Assert.Contains(result.Warnings, w => w.StartsWith(AgentInvoiceExtractor.RoundLimitWarning, StringComparison.Ordinal));
    }

    private static LatencyStampingChatClient Client(HttpMessageHandler handler) =>
        new LatencyStampingChatClient(new AnthropicClient { ApiKey = "test", MaxRetries = 0, HttpClient = new HttpClient(handler) }.AsIChatClient("claude-haiku-4-5"));

    /// <summary>Returns the scripted content blocks in order (tool_use blocks get stop_reason tool_use) and records each request body.</summary>
    private sealed class ScriptedHandler(params object[][] turns) : HttpMessageHandler
    {
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            var content = turns[Bodies.Count - 1];
            var message = JsonSerializer.Serialize(new
            {
                id = $"msg_{Bodies.Count}", type = "message", role = "assistant", model = "claude-test", content,
                stop_reason = content.Any(c => JsonSerializer.Serialize(c).Contains("\"tool_use\"", StringComparison.Ordinal)) ? "tool_use" : "end_turn",
                stop_sequence = (string?)null,
                usage = new { input_tokens = 1000, output_tokens = 50 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        }
    }
}
