using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceEvals.Cli.Langfuse;

/// <summary>
/// The Langfuse public REST endpoints evals uses: datasets, dataset items, scores. Transient failures (network,
/// timeout, 408, 429, 5xx) are retried with exponential backoff honouring Retry-After; every write is idempotent
/// (dataset items upsert on id, scores on id), so a retry never duplicates data.
/// </summary>
internal sealed class LangfuseClient : IDisposable
{
    public const int MaxAttempts = 4;

    /// <summary>Scores per POST; far below the API's body limit even with long comments.</summary>
    public const int ScoreBatchSize = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient http;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public LangfuseClient(LangfuseOptions options, HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = options.BaseUrl;
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", options.BasicAuth);
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>The dataset's id, or null if no dataset has that name.</summary>
    public async Task<string?> GetDatasetIdAsync(string name, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"api/public/v2/datasets/{Uri.EscapeDataString(name)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return (await ReadAsync<IdResponse>(response, ct)).Id;
    }

    public async Task<string> CreateDatasetAsync(string name, string description, object metadata, CancellationToken ct)
    {
        using var response = await SendAsync(() => Post("api/public/v2/datasets", new { name, description, metadata }), ct);
        return (await ReadAsync<IdResponse>(response, ct)).Id;
    }

    /// <summary>Creates or updates (by id) a dataset item; returns the stored id.</summary>
    public async Task<string> UpsertDatasetItemAsync(DatasetItem item, CancellationToken ct)
    {
        using var response = await SendAsync(() => Post("api/public/dataset-items", item), ct);
        return (await ReadAsync<IdResponse>(response, ct)).Id;
    }

    /// <summary>Posts scores in batches. A 207 (partial rejection) is reported, not retried, as the API asks.</summary>
    public async Task<ScoreBatchResult> CreateScoresAsync(IReadOnlyList<Score> scores, CancellationToken ct)
    {
        var (accepted, rejected, errors) = (0, 0, new List<string>());
        foreach (var batch in scores.Chunk(ScoreBatchSize))
        {
            using var response = await SendAsync(() => Post("api/public/scores", batch), ct);
            if (response.StatusCode == HttpStatusCode.MultiStatus)
            {
                var result = await response.Content.ReadFromJsonAsync<MultiStatus>(Json, ct);
                accepted += result?.Accepted ?? 0;
                rejected += result?.Rejected ?? batch.Length;
                errors.AddRange(result?.Errors?.Select(e => e.Message) ?? ["207 without a body"]);
                continue;
            }
            await EnsureSuccessAsync(response, ct);
            accepted += batch.Length;
        }
        return new ScoreBatchResult(accepted, rejected, [.. errors.Distinct(StringComparer.Ordinal)]);
    }

    private static HttpRequestMessage Post<T>(string path, T body) => new(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: Json) };

    /// <summary>Sends with retries. Returns the final response; 404 is returned for the caller to interpret.</summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan? retryAfter = null;
            try
            {
                using var message = request();
                var response = await http.SendAsync(message, ct);
                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts) return response;
                retryAfter = RetryAfter(response);
                response.Dispose();
            }
            catch (HttpRequestException) when (attempt < MaxAttempts)
            {
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                // HttpClient.Timeout elapsed.
            }
            await delay(retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return wait is { } w ? TimeSpan.FromSeconds(Math.Clamp(w.TotalSeconds, 0, 60)) : null;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
            ?? throw new LangfuseException($"{(int)response.StatusCode} from {response.RequestMessage?.RequestUri}: empty body.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new LangfuseException(string.Create(CultureInfo.InvariantCulture,
            $"{(int)response.StatusCode} {response.ReasonPhrase} from {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {(body.Length > 500 ? body[..500] + "…" : body)}"));
    }

    public void Dispose() => http.Dispose();

    private sealed record IdResponse(string Id);

    private sealed record MultiStatus(int Accepted, int Rejected, IReadOnlyList<MultiStatusError>? Errors);

    private sealed record MultiStatusError(string Message);
}

/// <param name="Status">"ACTIVE" or "ARCHIVED"; null keeps the server default.</param>
internal sealed record DatasetItem(string Id, string DatasetName, object? Input, object? ExpectedOutput, object? Metadata, string? Status = null);

/// <param name="ObservationId">The experiment item's root span id.</param>
internal sealed record Score(string Id, string TraceId, string ObservationId, string Name, double Value, string? Comment, string Environment, string DataType = "NUMERIC");

internal sealed record ScoreBatchResult(int Accepted, int Rejected, IReadOnlyList<string> Errors);

internal sealed class LangfuseException : Exception
{
    public LangfuseException()
    {
    }

    public LangfuseException(string message) : base(message)
    {
    }

    public LangfuseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
