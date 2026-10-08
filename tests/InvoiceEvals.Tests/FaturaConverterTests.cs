using InvoiceEvals.Core;
using InvoiceEvals.Core.Fatura;

namespace InvoiceEvals.Tests;

/// <summary>Expected values were checked by hand against the rendered invoice images.</summary>
public sealed class FaturaConverterTests
{
    [Fact]
    public void Template1_LogoOnlyVendor_VatDiscountEur()
    {
        var actual = Convert("Template1_Instance0");

        var address = new Address("16424 Timothy Mission", "Markville", "AK", "58294", "US");
        Assert.Equivalent(new InvoiceDto(
            Vendor: new Party(null, address),
            Customer: new Party("Denise Perez", address),
            InvoiceNumber: null,
            InvoiceDate: new DateOnly(2008, 3, 20),
            DueDate: new DateOnly(2016, 10, 16),
            Currency: "EUR",
            Subtotal: 725.30m,
            Discount: 13.42m,
            Tax: 28.18m,
            Total: 734.33m,
            LineItems: null), actual, strict: true);
    }

    [Fact]
    public void Template25_MultipleGstLines_AreSummedIntoTax()
    {
        var actual = Convert("Template25_Instance126");

        Assert.Equivalent(new InvoiceDto(
            Vendor: new Party("Kelly-Moss", new Address("6030 Willie Shores Suite 354", "Guerraton", "TX", "60058", "US")),
            Customer: new Party("Pamela Harris", new Address("0999 Antonio Rapids Apt. 810", "Hubbardstad", "TN", "60201", "US")),
            InvoiceNumber: "4411-929",
            InvoiceDate: new DateOnly(2012, 1, 27),
            DueDate: new DateOnly(2020, 8, 31),
            Currency: "USD",
            Subtotal: 600.02m,
            Discount: null,
            Tax: 30.00m + 72.00m + 108.00m + 120.00m + 6.00m,
            Total: 616.52m,
            LineItems: null), actual, strict: true);
    }

    [Fact]
    public void Template27_BillToWinsOverShipTo_NoAmounts()
    {
        var actual = Convert("Template27_Instance7");

        Assert.Equivalent(new InvoiceDto(
            Vendor: null,
            Customer: new Party("Brian Miller", new Address("230 Christopher Parkway Apt. 320", "East Suzanne", "WV", "13980", "US")),
            InvoiceNumber: "INV/72-97/395",
            InvoiceDate: new DateOnly(2002, 8, 29),
            DueDate: null,
            Currency: null,
            Subtotal: null,
            Discount: null,
            Tax: null,
            Total: null,
            LineItems: null), actual, strict: true);
    }

    [Theory]
    [InlineData("$", "USD")]
    [InlineData("USD", "USD")]
    [InlineData("EUR", "EUR")]
    public void Currency_IsNormalizedToIso4217(string printed, string expected)
    {
        var actual = FaturaConverter.Convert($$$"""{"TOTAL": {"text": "TOTAL : 10.00 {{{printed}}}"}}""");

        Assert.Equal(expected, actual.Currency);
    }

    [Fact]
    public void DateLabelledAsDueDate_IsRejected() =>
        Assert.Throws<FormatException>(() => FaturaConverter.Convert("""{"DATE": {"text": "Due Date : 08-Mar-2020"}}"""));

    [Fact]
    public void ConflictingCurrencies_AreRejected() =>
        Assert.Throws<FormatException>(() => FaturaConverter.Convert(
            """{"TOTAL": {"text": "TOTAL : 10.00 EUR"}, "SUB_TOTAL": {"text": "SUB_TOTAL : 9.00  USD"}}"""));

    private static InvoiceDto Convert(string name) =>
        FaturaConverter.Convert(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "fatura", $"{name}.json")));
}
