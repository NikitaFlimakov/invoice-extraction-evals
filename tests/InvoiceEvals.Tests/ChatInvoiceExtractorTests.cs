using System.Net;
using System.Text;
using System.Text.Json;

using Anthropic;

using InvoiceEvals.Core;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Tests;

/// <summary>Offline: the Anthropic SDK talks to a fake handler that records the request body and returns a canned message.</summary>
public sealed class ChatInvoiceExtractorTests
{
    private static readonly PromptTemplate Prompt = new("plain", "1.0", "test", "Extract the invoice.\n");
    private static readonly EvalDocument Doc = new(
        new GoldenDocument("fatura/X", "fatura", "T1", "golden/fatura/X.jpg", "golden/fatura/X.txt", null,
            new InvoiceDto(null, null, "INV-1", null, null, null, null, null, null, 10m, null)),
        "INVOICE INV-1\nTOTAL 10.00 USD\n");

    [Fact]
    public async Task DisableThinking_SendsBetweenTools_JsonSchema_AndNoTemperature()
    {
        var (handler, chat) = Client("""{"invoiceNumber":"INV-1","total":10.00}""");
        var config = new RunConfig("strong-plain", "claude-sonnet-5-5", "plain", "text", Temperature: null, DisableThinking: true);

        var result = await new ChatInvoiceExtractor(chat, config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("claude-sonnet-5-5", body.GetProperty("model").GetString());
        Assert.Equal("between_tools", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("json_schema", body.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.Contains("Extract the invoice.", body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("INV-1", result.Invoice!.InvoiceNumber);
        Assert.Equal(10.00m, result.Invoice.Total);
        Assert.Null(result.ParseError);
        Assert.Equal(1234, result.InputTokens);
        Assert.Equal("1.0", result.PromptVersion);
    }

    [Fact]
    public async Task Temperature_IsSent_AndThinkingIsNotTouched()
    {
        var (handler, chat) = Client("""{"total":10.00}""");
        var config = new RunConfig("mini-plain", "claude-haiku-4-5", "plain", "text", Temperature: 0);

        await new ChatInvoiceExtractor(chat, config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal(0, body.GetProperty("temperature").GetDouble());
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task UnparsableResponse_IsReturnedWithParseError()
    {
        var (_, chat) = Client("""{"invoiceDate":"15/03/2024"}""");
        var config = new RunConfig("mini-plain", "claude-haiku-4-5", "plain", "text", 0);

        var result = await new ChatInvoiceExtractor(chat, config, Prompt).ExtractAsync(Doc, TestContext.Current.CancellationToken);

        Assert.Null(result.Invoice);
        Assert.Contains("invoiceDate", result.ParseError, StringComparison.Ordinal);
        Assert.Equal("""{"invoiceDate":"15/03/2024"}""", result.RawText);
    }

    private static (RecordingHandler Handler, IChatClient Chat) Client(string responseJson)
    {
        var handler = new RecordingHandler(responseJson);
        var client = new AnthropicClient { ApiKey = "test", MaxRetries = 0, HttpClient = new HttpClient(handler) };
        return (handler, new LatencyStampingChatClient(client.AsIChatClient("claude-haiku-4-5")));
    }

    private sealed class RecordingHandler(string text) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var message = JsonSerializer.Serialize(new
            {
                id = "msg_1", type = "message", role = "assistant", model = "claude-test",
                content = new[] { new { type = "text", text } },
                stop_reason = "end_turn", stop_sequence = (string?)null,
                usage = new { input_tokens = 1234, output_tokens = 56 },
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(message, Encoding.UTF8, "application/json") };
        }
    }
}
