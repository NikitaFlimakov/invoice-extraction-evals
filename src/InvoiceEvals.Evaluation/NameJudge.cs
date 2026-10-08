using System.Text.Json;

using InvoiceEvals.Core;
using InvoiceEvals.Extraction;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace InvoiceEvals.Evaluation;

/// <summary>A judge verdict. <see cref="Equivalent"/> is null when the response did not parse; <see cref="Reason"/> then says why.</summary>
public sealed record NameJudgement(bool? Equivalent, string Reason);

/// <summary>
/// LLM judge for the vendor/customer-name gray zone: called only when strict normalized equality failed and both
/// names are non-null. One structured-output call per pair, temperature 0, prompt from evals/judge/judge_prompt.md.
/// The caller supplies the chat client; inside <c>evals run</c> that is the Reporting client, so verdicts are cached
/// and replayed like extractions. Calibration and rules: docs/metrics.md.
/// </summary>
public sealed class NameJudge(PromptTemplate prompt, string model = NameJudge.DefaultModel)
{
    public const string DefaultModel = "claude-haiku-4-5";

    /// <summary>Lines of text-layer context on each side of the line that holds the name.</summary>
    public const int ExcerptContextLines = 3;

    public const int MaxExcerptChars = 1500;

    private const int MaxOutputTokens = 512;

    // Reason before verdict: the schema's property order is the generation order.
    private sealed record Verdict(string Reason, bool Equivalent);

    private static readonly ChatResponseFormat Schema = ChatResponseFormat.ForJsonSchema(
        AIJsonUtilities.CreateJsonSchema(typeof(Verdict), serializerOptions: GoldenSet.JsonOptions),
        "name_judgement", "Whether the extracted name denotes the same party as the annotated name.");

    public string Model => model;

    public string PromptVersion => prompt.Version;

    public ChatOptions Options => new() { ModelId = model, Temperature = 0, MaxOutputTokens = MaxOutputTokens, ResponseFormat = Schema };

    /// <param name="field">"vendor_name" or "customer_name".</param>
    /// <param name="textLayer">The document's text layer; the judge sees an excerpt around the annotated name.</param>
    public async Task<NameJudgement> JudgeAsync(IChatClient chat, string field, string golden, string predicted, string textLayer, CancellationToken ct)
    {
        var response = await new TracingChatClient(chat).GetResponseAsync(Messages(field, golden, predicted, textLayer), Options, ct);
        return Parse(response.Text);
    }

    public IReadOnlyList<ChatMessage> Messages(string field, string golden, string predicted, string textLayer) =>
    [
        new(ChatRole.System, prompt.Body),
        new(ChatRole.User,
            $"Field: {(field == "customer_name" ? "customer (bill-to) name" : "vendor (seller) name")}\n" +
            $"Annotated name: {golden}\n" +
            $"Extracted name: {predicted}\n\n" +
            $"Document text excerpt:\n<<<\n{Excerpt(textLayer, golden, predicted)}\n>>>\n"),
    ];

    /// <summary>
    /// Tolerant parse of <c>{"reason": string, "equivalent": bool}</c>: code fences and surrounding prose are
    /// ignored, "true"/"false" strings are accepted. Anything else yields a null verdict, never an exception.
    /// </summary>
    public static NameJudgement Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, "unparsed: empty response");
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start) return new(null, "unparsed: no JSON object");
        try
        {
            using var doc = JsonDocument.Parse(text.AsMemory(start, end - start + 1));
            var root = doc.RootElement;
            var reason = Property(root, "reason") is { ValueKind: JsonValueKind.String } r ? r.GetString()!.Trim() : "";
            bool? equivalent = Property(root, "equivalent") switch
            {
                { ValueKind: JsonValueKind.True } => true,
                { ValueKind: JsonValueKind.False } => false,
                { ValueKind: JsonValueKind.String } s when bool.TryParse(s.GetString(), out var b) => b,
                _ => null,
            };
            return equivalent is null ? new(null, "unparsed: no boolean 'equivalent'") : new(equivalent, reason);
        }
        catch (JsonException ex)
        {
            return new(null, $"unparsed: {ex.Message}");
        }
    }

    private static JsonElement? Property(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    /// <summary>
    /// The lines around the first line that contains the annotated name (else the predicted name; case-insensitive,
    /// then normalized), or the top of the document when neither is found. Deterministic, so it is a stable part of
    /// the cache key.
    /// </summary>
    public static string Excerpt(string textLayer, string golden, string? predicted)
    {
        var lines = textLayer.ReplaceLineEndings("\n").Split('\n');
        var hit = FindLine(lines, golden);
        if (hit < 0 && predicted is not null) hit = FindLine(lines, predicted);
        var (from, to) = hit < 0
            ? (0, Math.Min(lines.Length, (2 * ExcerptContextLines) + 1) - 1)
            : (Math.Max(0, hit - ExcerptContextLines), Math.Min(lines.Length - 1, hit + ExcerptContextLines));
        var excerpt = string.Join('\n', lines[from..(to + 1)]).Trim();
        return excerpt.Length <= MaxExcerptChars ? excerpt : excerpt[..MaxExcerptChars];
    }

    private static int FindLine(string[] lines, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return -1;
        var exact = Array.FindIndex(lines, l => l.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact >= 0) return exact;
        var normalized = FieldAccuracyEvaluator.NormalizeName(value);
        return normalized.Length == 0 ? -1 : Array.FindIndex(lines, l => FieldAccuracyEvaluator.NormalizeName(l).Contains(normalized, StringComparison.Ordinal));
    }
}

/// <summary>
/// The document's text layer for <see cref="NameJudge"/>, always the "text" layer whatever the input mode.
/// <paramref name="vendorNameInModelInput"/> is false when the model's input cannot contain the vendor name
/// (FATURA in ocr mode, see docs/annotation-guidelines.md); the judge is then not asked about the vendor.
/// </summary>
public sealed class DocumentTextContext(string textLayer, bool vendorNameInModelInput) : EvaluationContext(ContextName, textLayer)
{
    public const string ContextName = "Document text layer";

    public string TextLayer { get; } = textLayer;

    public bool VendorNameInModelInput { get; } = vendorNameInModelInput;
}
