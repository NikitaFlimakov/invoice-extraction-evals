using InvoiceEvals.Core.Fatura;

namespace InvoiceEvals.Tests;

public sealed class FaturaTextTests
{
    [Fact]
    public void Spans_AreInReadingOrder_WithoutOcrText()
    {
        var text = FaturaText.Spans(Read("Template1_Instance0"));
        var lines = text.Split('\n');

        Assert.Equal("TAX INVOICE", lines[0]);
        Assert.True(text.IndexOf("Date: 20-Mar-2008", StringComparison.Ordinal) < text.IndexOf("TOTAL : 734.33 EUR", StringComparison.Ordinal));
        Assert.Contains("Email:melvin40@example.net", text, StringComparison.Ordinal);
        Assert.DoesNotContain("melvind0", text, StringComparison.Ordinal); // OCR misread lives only in OTHER.
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ocr_IsTheFullPageText_IncludingTheItemTable()
    {
        var text = FaturaText.Ocr(Read("Template1_Instance0"));

        Assert.Contains("Data score fire. 6.00", text, StringComparison.Ordinal);
        Assert.Contains("melvind0", text, StringComparison.Ordinal);
    }

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "fatura", $"{name}.json"));
}
