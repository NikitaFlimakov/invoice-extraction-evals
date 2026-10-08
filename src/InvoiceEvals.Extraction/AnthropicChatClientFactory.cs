using Anthropic;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

/// <summary>Builds the provider IChatClient from EVALS_API_KEY and EVALS_BASE_URL (optional).</summary>
public static class AnthropicChatClientFactory
{
    /// <summary>The SDK retries transient failures (408/409/429/5xx, connection errors) with backoff. Parse failures are not HTTP errors and are never retried.</summary>
    public const int MaxRetries = 3;

    public static IChatClient Create(string defaultModel)
    {
        var apiKey = Environment.GetEnvironmentVariable("EVALS_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("EVALS_API_KEY is not set. Export an Anthropic API key (and optionally EVALS_BASE_URL) before `evals run`.");

        var baseUrl = Environment.GetEnvironmentVariable("EVALS_BASE_URL");
        var client = string.IsNullOrWhiteSpace(baseUrl)
            ? new AnthropicClient { ApiKey = apiKey, MaxRetries = MaxRetries }
            : new AnthropicClient { ApiKey = apiKey, MaxRetries = MaxRetries, BaseUrl = baseUrl };
        return client.AsIChatClient(defaultModel);
    }
}
