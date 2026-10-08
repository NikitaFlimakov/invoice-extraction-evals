namespace InvoiceEvals.Core;

/// <summary>One line of evals/annotations.jsonl.</summary>
/// <param name="Id">Stable identifier, e.g. "fatura/Template1_Instance0".</param>
/// <param name="Source">"fatura" or "synthetic".</param>
/// <param name="Layout">Template or edge-case family; the stratification key.</param>
/// <param name="DocumentPath">Path relative to the evals directory, forward slashes.</param>
public sealed record GoldenDocument(string Id, string Source, string Layout, string DocumentPath, InvoiceDto Expected);
