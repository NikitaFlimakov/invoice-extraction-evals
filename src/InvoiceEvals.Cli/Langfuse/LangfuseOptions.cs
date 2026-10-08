using System.Text;

namespace InvoiceEvals.Cli.Langfuse;

/// <summary>Langfuse connection from LANGFUSE_PUBLIC_KEY, LANGFUSE_SECRET_KEY and LANGFUSE_BASE_URL. Absent = tracing off.</summary>
internal sealed class LangfuseOptions(Uri baseUrl, string publicKey, string secretKey)
{
    public const string DatasetName = "invoice-extraction-evals";

    /// <summary>Langfuse environment for experiment traces and scores.</summary>
    public const string Environment = "experiment";

    private static readonly string[] Variables = ["LANGFUSE_PUBLIC_KEY", "LANGFUSE_SECRET_KEY", "LANGFUSE_BASE_URL"];

    /// <summary>Base URL with a trailing slash, so relative API paths resolve under it.</summary>
    public Uri BaseUrl { get; } = baseUrl;

    public string PublicKey { get; } = publicKey;

    public string BasicAuth { get; } = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{publicKey}:{secretKey}"));

    public Uri OtlpTracesEndpoint => new(BaseUrl, "api/public/otel/v1/traces");

    /// <summary>Null when none of the variables is set; null with a message on <paramref name="log"/> when only some are, or the URL is invalid.</summary>
    public static LangfuseOptions? FromEnvironment(TextWriter log)
    {
        var values = Variables.Select(v => System.Environment.GetEnvironmentVariable(v)?.Trim()).ToArray();
        if (values.All(string.IsNullOrEmpty)) return null;
        var missing = Variables.Where((_, i) => string.IsNullOrEmpty(values[i])).ToList();
        if (missing.Count > 0)
        {
            log.WriteLine($"Langfuse disabled: {string.Join(", ", missing)} not set.");
            return null;
        }
        if (!Uri.TryCreate(values[2]!.TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http"))
        {
            log.WriteLine($"Langfuse disabled: LANGFUSE_BASE_URL '{values[2]}' is not an http(s) URL.");
            return null;
        }
        return new LangfuseOptions(baseUrl, values[0]!, values[1]!);
    }

    public override string ToString() => $"{BaseUrl} ({PublicKey})";
}
