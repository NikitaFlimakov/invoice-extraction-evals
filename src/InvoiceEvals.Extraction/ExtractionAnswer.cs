using InvoiceEvals.Core;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Extraction;

/// <summary>
/// Which text of a response is the extraction answer. A direct extraction has one assistant message, so this is its
/// text. An agent response also holds tool-call turns; the answer is the last assistant text that parses as
/// <see cref="InvoiceDto"/> (the best answer so far if the tool-round limit cut the loop short), else the last assistant
/// text, so a parse error still reports what the model said.
/// </summary>
public static class ExtractionAnswer
{
    public static string Text(ChatResponse response)
    {
        var texts = response.Messages.Where(m => m.Role == ChatRole.Assistant).Select(m => m.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (texts.Count == 0) return response.Text;
        for (var i = texts.Count - 1; i >= 0; i--)
        {
            if (InvoiceJson.TryParse(texts[i], out _, out _)) return texts[i];
        }
        return texts[^1];
    }
}
