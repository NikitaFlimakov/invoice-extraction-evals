using System.Text;
using System.Text.Json;

namespace InvoiceEvals.Core.Fatura;

/// <summary>Builds the model's text input from a FATURA "Original_Format" annotation. See docs/annotation-guidelines.md.</summary>
public static class FaturaText
{
    /// <summary>
    /// "text" mode: every labelled span's text in reading order (top to bottom, then left to right by bounding box).
    /// The item table is annotated only as a box, so its contents are absent.
    /// </summary>
    public static string Spans(string annotationJson)
    {
        using var doc = JsonDocument.Parse(annotationJson);
        var spans = doc.RootElement.EnumerateObject()
            .Where(p => p.Name != "OTHER" && p.Value.ValueKind == JsonValueKind.Object
                && p.Value.TryGetProperty("text", out _) && p.Value.TryGetProperty("bbox", out _))
            .Select(p => (Text: p.Value.GetProperty("text").GetString()!.Trim(), Box: Box(p.Value.GetProperty("bbox"))))
            .Where(s => s.Text.Length > 0)
            .OrderByDescending(s => s.Box.Top) // PDF coordinates: y grows upwards.
            .ThenBy(s => s.Box.Left)
            .ThenBy(s => s.Text, StringComparer.Ordinal);
        return Join(spans.Select(s => s.Text));
    }

    /// <summary>"ocr" mode: FATURA's full-page OCR text (the OTHER key), noise included.</summary>
    public static string Ocr(string annotationJson)
    {
        using var doc = JsonDocument.Parse(annotationJson);
        var text = doc.RootElement.TryGetProperty("OTHER", out var other) && other.TryGetProperty("text", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("OTHER: no OCR text.");
        return Join(text.Split('\n').Select(l => l.TrimEnd()));
    }

    private static (double Left, double Top) Box(JsonElement bbox)
    {
        var (x1, y1) = (bbox[0][0].GetDouble(), bbox[0][1].GetDouble());
        var (x2, y2) = (bbox[1][0].GetDouble(), bbox[1][1].GetDouble());
        return (Math.Min(x1, x2), Math.Max(y1, y2));
    }

    private static string Join(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(line.Replace("\r", "", StringComparison.Ordinal)).Append('\n');
        return sb.ToString().TrimEnd('\n') + "\n";
    }
}
