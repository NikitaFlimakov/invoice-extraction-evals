using System.Globalization;
using System.Text;

using InvoiceEvals.Core;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace InvoiceEvals.Synthetic;

/// <summary>Renders a <see cref="SyntheticInvoice"/> to PNG pages (QuestPDF) and to a text layer in reading order.</summary>
public static class InvoiceRenderer
{
    static InvoiceRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.UseSystemFonts = false; // Bundled Lato only, so output does not depend on installed fonts.
    }

    /// <summary>The blocks of the page, top to bottom and left to right. The image and the text layer both use these.</summary>
    private static (string[] VendorBlock, string[] MetaBlock, string[] BillTo, string[]? ShipTo, string[][] Rows, (string Label, string Value)[] Totals) Blocks(SyntheticInvoice inv)
    {
        var m = inv.Money;
        string Date(DateOnly d) => d.ToString(inv.DateFormat, CultureInfo.InvariantCulture);
        var totals = new List<(string, string)> { ("Subtotal", m.Format(inv.Subtotal)) };
        if (inv.Discount is { } discount) totals.Add((inv.DiscountLabel!, m.Format(-discount)));
        totals.AddRange(inv.TaxLines.Select(t => (t.Label, m.Format(t.Amount))));
        totals.Add((inv.Total < 0 ? "Total credit" : "Total due", m.Format(inv.Total)));

        return (
            [inv.Vendor.Name!, .. AddressLines(inv.Vendor.Address!), inv.VendorTaxId],
            [inv.Title, $"{inv.NumberLabel} {inv.Number}", $"Date: {Date(inv.InvoiceDate)}", $"Due date: {Date(inv.DueDate)}"],
            ["Bill to:", inv.Customer.Name!, .. AddressLines(inv.Customer.Address!)],
            inv.ShipTo is null ? null : ["Ship to:", inv.ShipTo.Name!, .. AddressLines(inv.ShipTo.Address!)],
            [.. inv.Lines.Select(l => new[] { l.Description, l.Quantity!.Value.ToString("0", CultureInfo.InvariantCulture), m.Format(l.UnitPrice!.Value), m.Format(l.Amount!.Value) })],
            [.. totals]);
    }

    public static string Text(SyntheticInvoice inv)
    {
        var (vendor, meta, billTo, shipTo, rows, totals) = Blocks(inv);
        var sb = new StringBuilder();
        void Block(IEnumerable<string> lines) { foreach (var l in lines) sb.Append(l).Append('\n'); sb.Append('\n'); }
        Block(vendor);
        Block(meta);
        Block(billTo);
        if (shipTo is not null) Block(shipTo);
        Block([string.Join(" | ", TableHeader), .. rows.Select(r => string.Join(" | ", r))]);
        Block(totals.Select(t => $"{t.Label}: {t.Value}"));
        Block(inv.Notes);
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    public static IReadOnlyList<byte[]> RenderPng(SyntheticInvoice inv)
    {
        var (vendor, meta, billTo, shipTo, rows, totals) = Blocks(inv);
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(40);
            page.DefaultTextStyle(s => s.FontSize(10).FontFamily("Lato"));
            page.Content().Column(col =>
            {
                col.Spacing(18);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(vendor[0]).Bold().FontSize(13);
                        foreach (var l in vendor[1..]) c.Item().Text(l);
                    });
                    row.RelativeItem().AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(meta[0]).Bold().FontSize(18);
                        foreach (var l in meta[1..]) c.Item().AlignRight().Text(l);
                    });
                });
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c => Lines(c, billTo));
                    row.RelativeItem().Column(c => { if (shipTo is not null) Lines(c, shipTo); });
                });
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(5); c.RelativeColumn(1); c.RelativeColumn(2); c.RelativeColumn(2); });
                    table.Header(h =>
                    {
                        for (var i = 0; i < TableHeader.Length; i++)
                            h.Cell().BorderBottom(1).PaddingBottom(3).AlignRight(i > 0).Text(TableHeader[i]).Bold();
                    });
                    foreach (var r in rows)
                        for (var i = 0; i < r.Length; i++)
                            table.Cell().PaddingVertical(2).AlignRight(i > 0).Text(r[i]);
                });
                col.Item().AlignRight().Width(260).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); });
                    foreach (var (label, value) in totals)
                    {
                        var last = label == totals[^1].Label;
                        table.Cell().PaddingVertical(1).Text(label).Bold(last);
                        table.Cell().PaddingVertical(1).AlignRight().Text(value).Bold(last);
                    }
                });
                col.Item().Column(c => { foreach (var n in inv.Notes) c.Item().Text(n).FontSize(9); });
            });
        }));
        return [.. document.GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = 96 })];
    }

    private static readonly string[] TableHeader = ["Description", "Qty", "Unit price", "Amount"];

    private static void Lines(ColumnDescriptor c, string[] lines)
    {
        c.Item().Text(lines[0]).Bold();
        foreach (var l in lines[1..]) c.Item().Text(l);
    }

    private static IContainer AlignRight(this IContainer container, bool right) => right ? container.AlignRight() : container;

    private static TextSpanDescriptor Bold(this TextSpanDescriptor text, bool bold) => bold ? text.Bold() : text;

    private static IEnumerable<string> AddressLines(Address a) =>
    [
        a.Street!,
        a.Region is null ? $"{a.PostalCode} {a.City}" : $"{a.City}, {a.Region} {a.PostalCode}",
        CountryNames[a.Country!],
    ];

    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.Ordinal)
    {
        ["DE"] = "Germany", ["GB"] = "United Kingdom", ["FR"] = "France", ["JP"] = "Japan", ["CH"] = "Switzerland", ["CA"] = "Canada",
        ["US"] = "United States", ["AU"] = "Australia", ["SE"] = "Sweden", ["IT"] = "Italy", ["NL"] = "Netherlands", ["FI"] = "Finland",
    };
}
