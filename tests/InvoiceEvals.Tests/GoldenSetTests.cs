using System.Text.Json;
using InvoiceEvals.Core;

namespace InvoiceEvals.Tests;

public sealed class GoldenSetTests
{
    private static readonly InvoiceDto FullInvoice = new(
        Vendor: new Party("Acme GmbH", new Address("Hauptstraße 1", "Berlin", "BE", "10115", "DE")),
        Customer: new Party("Jane Doe", null),
        InvoiceNumber: "INV/2024-001",
        InvoiceDate: new DateOnly(2024, 2, 29),
        DueDate: new DateOnly(2024, 3, 30),
        Currency: "EUR",
        Subtotal: 100.50m,
        Discount: 0.50m,
        Tax: 19.00m,
        Total: 119.00m,
        LineItems: [new LineItem("Widget", 2m, 50.25m, 100.50m), new LineItem("Note-only line", null, null, null)]);

    [Fact]
    public void InvoiceDto_RoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(FullInvoice, GoldenSet.JsonOptions);
        var back = JsonSerializer.Deserialize<InvoiceDto>(json, GoldenSet.JsonOptions);

        Assert.Equivalent(FullInvoice, back, strict: true);
    }

    [Fact]
    public void InvoiceDto_UsesCamelCaseIsoDatesAndExplicitNulls()
    {
        var json = JsonSerializer.Serialize(FullInvoice with { DueDate = null }, GoldenSet.JsonOptions);

        Assert.Contains("\"invoiceDate\":\"2024-02-29\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dueDate\":null", json, StringComparison.Ordinal);
        Assert.Contains("\"subtotal\":100.50", json, StringComparison.Ordinal);
        Assert.Contains("Hauptstraße", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips_AndOutputIsIndependentOfInputOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        GoldenDocument[] docs =
        [
            new("fatura/B", "fatura", "T2", "golden/fatura/B.jpg", "golden/fatura/B.txt", "golden/fatura/B.ocr.txt", FullInvoice),
            new("fatura/A", "fatura", "T1", "golden/fatura/A.jpg", "golden/fatura/A.txt", null, FullInvoice with { LineItems = null }),
        ];
        var path1 = Path.GetTempFileName();
        var path2 = Path.GetTempFileName();
        try
        {
            await GoldenSet.WriteAsync(path1, docs, ct);
            await GoldenSet.WriteAsync(path2, docs.Reverse(), ct);

            Assert.Equal(await File.ReadAllBytesAsync(path1, ct), await File.ReadAllBytesAsync(path2, ct));
            var read = await GoldenSet.ReadAsync(path1, ct);
            Assert.Equal(["fatura/A", "fatura/B"], read.Select(d => d.Id));
            Assert.Equivalent(docs.OrderBy(d => d.Id, StringComparer.Ordinal), read, strict: true);
            Assert.DoesNotContain("\r", await File.ReadAllTextAsync(path1, ct), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path1);
            File.Delete(path2);
        }
    }
}
