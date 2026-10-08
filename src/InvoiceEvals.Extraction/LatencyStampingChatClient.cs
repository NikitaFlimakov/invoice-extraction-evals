using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

/// <summary>
/// Sits below the response cache, so it only sees real network calls. Counts them, marks the current model-call span
/// as a cache miss, and stamps the measured latency into the response's AdditionalProperties so a cached response
/// still reports the original latency.
/// </summary>
public sealed class LatencyStampingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private const string Key = "evals_latency_ms";
    private int networkCalls;

    public int NetworkCalls => networkCalls;

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref networkCalls);
        Activity.Current?.SetTag(EvalsTelemetry.CacheHitTag, false); // The TracingChatClient span above the cache.
        var stopwatch = Stopwatch.StartNew();
        var response = await base.GetResponseAsync(messages, options, cancellationToken);
        (response.AdditionalProperties ??= [])[Key] = stopwatch.Elapsed.TotalMilliseconds;
        return response;
    }

    public static TimeSpan? ReadLatency(ChatResponse response) =>
        response.AdditionalProperties?.TryGetValue(Key, out var value) == true
            ? value switch
            {
                JsonElement { ValueKind: JsonValueKind.Number } e => TimeSpan.FromMilliseconds(e.GetDouble()),
                _ => TimeSpan.FromMilliseconds(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            }
            : null;
}
