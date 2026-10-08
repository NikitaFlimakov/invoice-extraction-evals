using InvoiceEvals.Core;

namespace InvoiceEvals.Synthetic;

/// <summary>
/// Everything printed on one synthetic invoice. The image, the text layer and the golden <see cref="InvoiceDto"/>
/// are all derived from this record, so they cannot disagree.
/// </summary>
public sealed record SyntheticInvoice(
    string Id,
    string Family,
    string Title,
    Party Vendor,
    string VendorTaxId,
    Party Customer,
    Party? ShipTo,
    string NumberLabel,
    string Number,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    string DateFormat,
    Money Money,
    IReadOnlyList<LineItem> Lines,
    string? DiscountLabel,
    decimal? Discount,
    IReadOnlyList<(string Label, decimal Amount)> TaxLines,
    IReadOnlyList<string> Notes)
{
    public decimal Subtotal => Lines.Sum(l => l.Amount!.Value);

    public decimal? Tax => TaxLines.Count == 0 ? null : TaxLines.Sum(t => t.Amount);

    public decimal Total => Subtotal - (Discount ?? 0) + (Tax ?? 0);

    public InvoiceDto ToDto() => new(
        Vendor, Customer, Number, InvoiceDate, DueDate, Money.Code, Subtotal, Discount, Tax, Total, Lines);
}

/// <summary>How amounts are printed: ISO code, symbol, decimals and separators.</summary>
public sealed record Money(string Code, string Symbol, int Decimals, string GroupSeparator, bool SymbolAfter)
{
    public string Format(decimal amount)
    {
        var digits = Math.Abs(amount).ToString(Decimals == 0 ? "#,0" : "#,0.00", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", GroupSeparator, StringComparison.Ordinal);
        var sign = amount < 0 ? "-" : "";
        return SymbolAfter ? $"{sign}{digits} {Symbol}" : $"{sign}{Symbol}{digits}";
    }
}
