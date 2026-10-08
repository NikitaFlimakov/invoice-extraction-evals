using System.Text.Json;

namespace InvoiceEvals.Core;

/// <summary>Parses a model response into an <see cref="InvoiceDto"/> with the golden set's JSON conventions.</summary>
public static class InvoiceJson
{
    public static bool TryParse(string text, out InvoiceDto? invoice, out string? error)
    {
        invoice = null;
        try
        {
            invoice = JsonSerializer.Deserialize<InvoiceDto>(text, GoldenSet.JsonOptions);
            error = invoice is null ? "Response is JSON null." : null;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
        }
        return invoice is not null;
    }
}
