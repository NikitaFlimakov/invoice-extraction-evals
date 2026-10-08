using System.Text.Json;

using InvoiceEvals.Core;

namespace InvoiceEvals.Cli;

/// <summary>Hand-maintained USD per million tokens per model, from evals/pricing.json.</summary>
internal sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok)
{
    /// <summary>Output tokens include thinking tokens; Anthropic bills them as output.</summary>
    public decimal Cost(double inputTokens, double outputTokens) =>
        ((decimal)inputTokens * InputPerMTok + (decimal)outputTokens * OutputPerMTok) / 1_000_000m;

    public static async Task<IReadOnlyDictionary<string, ModelPrice>> LoadAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.EnumerateObject()
            .Where(p => !p.Name.StartsWith('_'))
            .ToDictionary(p => p.Name, p => p.Value.Deserialize<ModelPrice>(GoldenSet.JsonOptions)!, StringComparer.Ordinal);
    }
}
