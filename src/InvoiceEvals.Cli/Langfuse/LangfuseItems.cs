using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InvoiceEvals.Cli.Langfuse;

/// <summary>
/// evals/langfuse-items.json: the Langfuse dataset id and the dataset-item id of every eval document, written by
/// <c>evals langfuse sync-dataset</c> and read by <c>evals run</c> to link traces to items.
/// </summary>
internal sealed record LangfuseItems(string Dataset, string DatasetId, IReadOnlyDictionary<string, string> Items)
{
    public const string FileName = "langfuse-items.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Deterministic item id: re-syncing upserts the same items instead of creating new ones.</summary>
    public static string ItemId(string docId) => "iee-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(docId)))[..24];

    public static LangfuseItems? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var items = JsonSerializer.Deserialize<LangfuseItems>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty.");
        return items with { Items = new Dictionary<string, string>(items.Items, StringComparer.Ordinal) };
    }

    /// <summary>Items ordered by document id (ordinal), LF line endings, so the file diffs cleanly.</summary>
    public async Task SaveAsync(string path, CancellationToken ct)
    {
        var ordered = this with { Items = Items.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) };
        var json = JsonSerializer.Serialize(ordered, Json).ReplaceLineEndings("\n") + "\n";
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }
}
