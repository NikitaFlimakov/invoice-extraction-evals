namespace InvoiceEvals.Core;

/// <summary>A golden document plus the text the model sees for the chosen input mode.</summary>
public sealed record EvalDocument(GoldenDocument Golden, string Text)
{
    public string Id => Golden.Id;

    /// <summary>The "text" layer (<see cref="GoldenDocument.TextPath"/>) whatever the input mode; what the name judge reads.</summary>
    public string TextLayer { get; init; } = Text;

    /// <summary>Reporting scenario name: the id with "/" replaced by "." (Reporting rejects path separators).</summary>
    public string ScenarioName => Id.Replace('/', '.');
}

/// <summary>The eval set: golden FATURA (evals/annotations.jsonl) plus synthetic (evals/synthetic/annotations.jsonl).</summary>
public static class EvalSet
{
    public const string TextMode = "text";
    public const string OcrMode = "ocr";

    public static async Task<IReadOnlyList<EvalDocument>> LoadAsync(string evalsDir, string inputMode, CancellationToken ct)
    {
        if (inputMode is not (TextMode or OcrMode)) throw new ArgumentException($"Unknown input mode '{inputMode}'.", nameof(inputMode));

        var golden = await GoldenSet.ReadAsync(Path.Combine(evalsDir, "annotations.jsonl"), ct);
        var synthetic = await GoldenSet.ReadAsync(Path.Combine(evalsDir, "synthetic", "annotations.jsonl"), ct);
        var docs = new List<EvalDocument>();
        foreach (var g in golden.Concat(synthetic).OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            // Synthetic documents have no OCR layer; ocr mode falls back to their generated text.
            var textLayer = await File.ReadAllTextAsync(Path.Combine(evalsDir, g.TextPath), ct);
            var text = inputMode == OcrMode && g.OcrTextPath is not null ? await File.ReadAllTextAsync(Path.Combine(evalsDir, g.OcrTextPath), ct) : textLayer;
            docs.Add(new EvalDocument(g, text) { TextLayer = textLayer });
        }
        return docs;
    }
}
