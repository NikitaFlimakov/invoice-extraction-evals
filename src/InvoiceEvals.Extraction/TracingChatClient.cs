using System.Diagnostics;
using System.Text.Json;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

/// <summary>
/// Emits one OpenTelemetry span per model call with GenAI semantic-convention attributes and token usage. Sits
/// <em>above</em> the response cache, so a cached replay is traced too; the span's duration is the original network
/// latency stamped by <see cref="LatencyStampingChatClient"/>, not the time the cache lookup took.
/// </summary>
public sealed class TracingChatClient(IChatClient inner, string providerName = "anthropic") : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var activity = EvalsTelemetry.Source.StartActivity($"chat {options?.ModelId}", ActivityKind.Client);
        if (activity is null) return await base.GetResponseAsync(messages, options, cancellationToken);

        var list = messages as IReadOnlyList<ChatMessage> ?? [.. messages];
        activity.SetTag("gen_ai.operation.name", "chat")
            .SetTag("gen_ai.provider.name", providerName)
            .SetTag("gen_ai.request.model", options?.ModelId)
            .SetTag("gen_ai.request.temperature", options?.Temperature)
            .SetTag("gen_ai.request.max_tokens", options?.MaxOutputTokens)
            .SetTag("langfuse.observation.type", "generation")
            .SetTag("langfuse.observation.input", JsonSerializer.Serialize(list.Select(m => new Turn(m.Role.Value, m.Text)), TurnJson))
            .SetTag(EvalsTelemetry.CacheHitTag, true); // The provider-level client flips this when it is actually reached.
        try
        {
            var response = await base.GetResponseAsync(list, options, cancellationToken);
            activity.SetTag("gen_ai.response.model", response.ModelId)
                .SetTag("gen_ai.response.id", response.ResponseId)
                .SetTag("gen_ai.response.finish_reasons", response.FinishReason is { } reason ? new[] { reason.Value } : null)
                .SetTag("gen_ai.usage.input_tokens", response.Usage?.InputTokenCount)
                .SetTag("gen_ai.usage.output_tokens", response.Usage?.OutputTokenCount)
                .SetTag("gen_ai.usage.reasoning_tokens", response.Usage?.ReasoningTokenCount)
                .SetTag("langfuse.observation.output", response.Text);
            if (LatencyStampingChatClient.ReadLatency(response) is { } latency)
                activity.SetEndTime(activity.StartTimeUtc + latency);
            return response;
        }
        catch (Exception ex)
        {
            activity.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity.AddException(ex);
            throw;
        }
    }

    private static readonly JsonSerializerOptions TurnJson = new(JsonSerializerDefaults.Web);

    private sealed record Turn(string Role, string Content);
}
