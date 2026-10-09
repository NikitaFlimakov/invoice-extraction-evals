using System.Diagnostics;
using System.Text.Json;

using Anthropic.Models.Messages;

using InvoiceEvals.Core;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

/// <summary>Extracts an invoice with one structured-output chat call: system prompt = prompt variant, user message = document text.</summary>
public sealed class ChatInvoiceExtractor(IChatClient chat, RunConfig config, PromptTemplate prompt) : IInvoiceExtractor
{
    private const int MaxOutputTokens = 8192;

    /// <summary>JSON schema of <see cref="InvoiceDto"/> with the golden set's JSON conventions. Declared before <see cref="Schema"/>, which reads it.</summary>
    public static JsonElement InvoiceSchema { get; } = AIJsonUtilities.CreateJsonSchema(typeof(InvoiceDto), serializerOptions: GoldenSet.JsonOptions);

    private static readonly ChatResponseFormat Schema = ChatResponseFormat.ForJsonSchema(InvoiceSchema, "invoice", "Invoice fields as printed.");

    /// <summary>Model, temperature, output limit, response format and thinking setting of <paramref name="config"/>; shared with the agent extractor.</summary>
    public static ChatOptions CreateOptions(RunConfig config, ChatResponseFormat responseFormat)
    {
        var options = new ChatOptions
        {
            ModelId = config.Model,
            Temperature = (float?)config.Temperature,
            MaxOutputTokens = MaxOutputTokens,
            ResponseFormat = responseFormat,
        };
        if (config.DisableThinking)
        {
            options.RawRepresentationFactory = _ => new MessageCreateParams
            {
                Model = config.Model,
                MaxTokens = MaxOutputTokens,
                Messages = [],
                Thinking = new ThinkingConfigBetweenTools(),
            };
        }
        return options;
    }

    public async Task<ExtractionResult> ExtractAsync(EvalDocument doc, CancellationToken ct)
    {
        List<ChatMessage> messages = [new(ChatRole.System, prompt.Body), new(ChatRole.User, doc.Text)];
        var options = CreateOptions(config, Schema);

        var stopwatch = Stopwatch.StartNew();
        var response = await chat.GetResponseAsync(messages, options, ct);
        stopwatch.Stop();

        _ = InvoiceJson.TryParse(response.Text, out var invoice, out var error);
        return new ExtractionResult(
            invoice, response.Text, error,
            response.ModelId ?? config.Model, prompt.Version,
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, response.Usage?.ReasoningTokenCount,
            LatencyStampingChatClient.ReadLatency(response) ?? stopwatch.Elapsed,
            messages, response);
    }
}
