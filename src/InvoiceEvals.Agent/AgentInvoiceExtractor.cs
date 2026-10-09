using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using InvoiceEvals.Core;
using InvoiceEvals.Extraction;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace InvoiceEvals.Agent;

/// <summary>
/// Extracts an invoice with a Microsoft Agent Framework <see cref="ChatClientAgent"/> that can call
/// <see cref="InvoiceTools"/>. Same model options, prompt rules and <see cref="InvoiceDto"/> schema as
/// <see cref="ChatInvoiceExtractor"/>; the answer schema adds one top-level <c>warnings</c> array, which
/// <see cref="InvoiceJson"/> ignores, so the deterministic evaluators score the answer unchanged.
/// </summary>
/// <remarks>
/// Pipeline, top to bottom: OpenTelemetryAgent (invoke_agent span) → ChatClientAgent → FunctionInvokingChatClient
/// (tool loop, execute_tool spans, at most <see cref="MaxToolRounds"/> rounds) → turn log → <paramref name="chat"/>
/// (the caller's traced, cached client). Every model turn therefore goes through the response cache on its own, and
/// because the tools are deterministic a replay sends exactly the cached requests.
/// </remarks>
public sealed class AgentInvoiceExtractor(IChatClient chat, RunConfig config, PromptTemplate prompt) : IInvoiceExtractor
{
    public const string AgentName = "invoice-extractor";

    /// <summary>Tool-call rounds per document; a round is one model turn that requests tools and their execution.</summary>
    public const int MaxToolRounds = 6;

    public const string WarningsProperty = "warnings";

    /// <summary>Warning added when the loop hit <see cref="MaxToolRounds"/>.</summary>
    public const string RoundLimitWarning = "round_limit:";

    /// <summary>Prefix the prompt asks the model to use for a totals inconsistency reported by validate_totals.</summary>
    public const string TotalsWarning = "totals_inconsistent:";

    /// <summary>The <see cref="InvoiceDto"/> schema plus a top-level <c>warnings</c> string array.</summary>
    public static JsonElement AnswerSchema { get; } = BuildAnswerSchema();

    private static readonly ChatResponseFormat Format = ChatResponseFormat.ForJsonSchema(AnswerSchema, "invoice", "Invoice fields as printed, plus warnings.");

    public async Task<ExtractionResult> ExtractAsync(EvalDocument doc, CancellationToken ct)
    {
        var turns = new TurnLog(chat);
        using var invoking = new FunctionInvokingChatClient(turns)
        {
            // At the limit FunctionInvokingChatClient stops executing tools and makes one more request without them.
            MaximumIterationsPerRequest = MaxToolRounds,
            AllowConcurrentInvocation = false,
        };
        var options = ChatInvoiceExtractor.CreateOptions(config, Format);
        options.Instructions = prompt.Body;
        options.Tools = [.. InvoiceTools.All];
        var inner = new ChatClientAgent(invoking, new ChatClientAgentOptions { Name = AgentName, ChatOptions = options, UseProvidedChatClientAsIs = true });
        using var agent = new OpenTelemetryAgent(inner, EvalsTelemetry.SourceName) { EnableSensitiveData = true };

        var user = new ChatMessage(ChatRole.User, doc.Text);
        var stopwatch = Stopwatch.StartNew();
        var run = await agent.RunAsync([user], cancellationToken: ct);
        stopwatch.Stop();

        var response = new ChatResponse([.. run.Messages])
        {
            ModelId = turns.ModelId ?? config.Model,
            Usage = turns.Usage,
            FinishReason = run.FinishReason,
        };
        LatencyStampingChatClient.Stamp(response, turns.Latency ?? stopwatch.Elapsed);

        var answer = ExtractionAnswer.Text(response);
        _ = InvoiceJson.TryParse(answer, out var invoice, out var error);
        List<string> warnings = [.. Warnings(answer)];
        if (turns.ForcedAnswer || response.Messages.LastOrDefault()?.Contents.OfType<FunctionCallContent>().Any() == true)
            warnings.Add($"{RoundLimitWarning} stopped after {turns.ToolRounds} tool-call rounds (limit {MaxToolRounds}); {(invoice is null ? "no answer parsed" : "returning the last parsed answer")}.");

        return new ExtractionResult(
            invoice, answer, error,
            response.ModelId!, prompt.Version,
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.ReasoningTokenCount,
            LatencyStampingChatClient.ReadLatency(response)!.Value,
            [new ChatMessage(ChatRole.System, prompt.Body), user], response)
        {
            Warnings = warnings,
        };
    }

    /// <summary>The <c>warnings</c> array of an answer, or empty when absent or unparseable.</summary>
    public static IReadOnlyList<string> Warnings(string answer)
    {
        try
        {
            using var json = JsonDocument.Parse(answer);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty(WarningsProperty, out var array) && array.ValueKind == JsonValueKind.Array
                ? [.. array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonElement BuildAnswerSchema()
    {
        var schema = JsonNode.Parse(ChatInvoiceExtractor.InvoiceSchema.GetRawText())!.AsObject();
        schema["properties"]!.AsObject()[WarningsProperty] = new JsonObject
        {
            ["description"] = "Short notes on problems found with the tools, e.g. totals_inconsistent: ...; empty if none.",
            ["type"] = "array",
            ["items"] = new JsonObject { ["type"] = "string" },
        };
        if (schema["required"] is JsonArray required) required.Add(WarningsProperty);
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>Per-document log of the model turns below the tool loop: tool rounds, summed usage and original latency.</summary>
    private sealed class TurnLog(IChatClient inner) : DelegatingChatClient(inner)
    {
        private TimeSpan latency;
        private bool hasLatency;

        public int ToolRounds { get; private set; }

        public UsageDetails? Usage { get; private set; }

        public string? ModelId { get; private set; }

        public TimeSpan? Latency => hasLatency ? latency : null;

        /// <summary>The tool loop hit its limit and asked for a final answer without tools.</summary>
        public bool ForcedAnswer { get; private set; }

        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
            if (options?.Tools is not { Count: > 0 } && history.Any(m => m.Contents.OfType<FunctionCallContent>().Any()))
            {
                // Anthropic rejects tool_use blocks in a request that defines no tools; keep them and forbid calling them.
                ForcedAnswer = true;
                options = options?.Clone() ?? new ChatOptions();
                options.Tools = [.. InvoiceTools.All];
                options.ToolMode = ChatToolMode.None;
            }
            var response = await base.GetResponseAsync(history, options, cancellationToken);
            if (response.Messages.Any(m => m.Contents.OfType<FunctionCallContent>().Any())) ToolRounds++;
            if (response.Usage is { } usage) (Usage ??= new UsageDetails()).Add(usage);
            ModelId ??= response.ModelId;
            if (LatencyStampingChatClient.ReadLatency(response) is { } turn)
            {
                latency += turn;
                hasLatency = true;
            }
            return response;
        }
    }
}
