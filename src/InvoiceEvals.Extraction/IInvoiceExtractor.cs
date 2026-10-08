using InvoiceEvals.Core;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

public interface IInvoiceExtractor
{
    Task<ExtractionResult> ExtractAsync(EvalDocument doc, CancellationToken ct);
}

/// <param name="Invoice">Parsed response, or null if it did not parse.</param>
/// <param name="Messages">The request, kept so evaluation and reporting see exactly what the model saw.</param>
/// <param name="Response">The raw chat response (possibly served from the response cache).</param>
/// <param name="Latency">Wall-clock latency of the original network call, preserved across cache hits.</param>
public sealed record ExtractionResult(
    InvoiceDto? Invoice,
    string RawText,
    string? ParseError,
    string ModelId,
    string PromptVersion,
    long? InputTokens,
    long? OutputTokens,
    long? ThinkingTokens,
    TimeSpan Latency,
    IReadOnlyList<ChatMessage> Messages,
    ChatResponse Response);
