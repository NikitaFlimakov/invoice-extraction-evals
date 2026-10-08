namespace InvoiceEvals.Core;

/// <summary>One line of an annotations.jsonl file.</summary>
/// <param name="Id">Stable identifier, e.g. "fatura/Template1_Instance0".</param>
/// <param name="Source">"fatura" or "synthetic".</param>
/// <param name="Layout">Template or edge-case family; the stratification key.</param>
/// <param name="DocumentPath">Path relative to the evals directory, forward slashes. First page for multi-page documents.</param>
/// <param name="TextPath">Text layer in reading order ("text" input mode), relative to the evals directory.</param>
/// <param name="OcrTextPath">Full-page OCR text ("ocr" input mode); null when the document has none (synthetic).</param>
public sealed record GoldenDocument(
    string Id, string Source, string Layout, string DocumentPath, string TextPath, string? OcrTextPath, InvoiceDto Expected);
