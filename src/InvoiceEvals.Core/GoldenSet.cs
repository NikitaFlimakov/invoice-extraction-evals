using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceEvals.Core;

/// <summary>Reads and writes the golden set as JSON Lines, one <see cref="GoldenDocument"/> per line.</summary>
public static class GoldenSet
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Writes documents ordered by id with LF line endings, so output is byte-identical across runs and OSes.</summary>
    public static async Task WriteAsync(string path, IEnumerable<GoldenDocument> documents, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var doc in documents.OrderBy(d => d.Id, StringComparer.Ordinal))
            sb.Append(JsonSerializer.Serialize(doc, JsonOptions)).Append('\n');
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
    }

    public static async Task<IReadOnlyList<GoldenDocument>> ReadAsync(string path, CancellationToken ct)
    {
        var docs = new List<GoldenDocument>();
        await foreach (var line in File.ReadLinesAsync(path, ct))
        {
            if (line.Length == 0) continue;
            docs.Add(JsonSerializer.Deserialize<GoldenDocument>(line, JsonOptions)
                ?? throw new InvalidDataException($"Null record in {path}"));
        }
        return docs;
    }
}
