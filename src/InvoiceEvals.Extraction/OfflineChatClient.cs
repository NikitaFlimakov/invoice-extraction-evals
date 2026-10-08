using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

/// <summary>
/// Provider stand-in for <c>evals run --offline</c>. It sits where the network client would, below the response
/// cache, so it is reached only on a cache miss, and then it throws <see cref="CacheMissException"/>.
/// </summary>
public sealed class OfflineChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new CacheMissException($"Response cache miss for model '{options?.ModelId ?? "default"}' in offline mode.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new CacheMissException($"Response cache miss for model '{options?.ModelId ?? "default"}' in offline mode.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A model call was not in the response cache while running offline.</summary>
public sealed class CacheMissException : Exception
{
    public CacheMissException()
    {
    }

    public CacheMissException(string message) : base(message)
    {
    }

    public CacheMissException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
