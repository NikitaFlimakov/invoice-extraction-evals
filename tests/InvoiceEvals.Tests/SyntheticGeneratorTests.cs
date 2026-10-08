using System.Text.Json;

using InvoiceEvals.Core;
using InvoiceEvals.Synthetic;

namespace InvoiceEvals.Tests;

public sealed class SyntheticGeneratorTests
{
    private static readonly IReadOnlyList<SyntheticInvoice> Set = SyntheticGenerator.Generate(seed: 42);

    [Fact]
    public void SameSeed_SameDtosAndTextLayers()
    {
        // PNG bytes are not compared: rasterisation may differ across OSes.
        var again = SyntheticGenerator.Generate(seed: 42);

        Assert.Equal(Set.Select(Snapshot), again.Select(Snapshot));
    }

    [Fact]
    public void DifferentSeed_DifferentContent() =>
        Assert.NotEqual(Set.Select(Snapshot), SyntheticGenerator.Generate(seed: 7).Select(Snapshot));

    [Fact]
    public void ThirtyCases_AcrossSevenFamilies_UniqueIdsAndVendors()
    {
        Assert.Equal(30, Set.Count);
        Assert.Equal(7, Set.Select(i => i.Family).Distinct().Count());
        Assert.Distinct(Set.Select(i => i.Id));
        Assert.Distinct(Set.Select(i => i.Vendor.Name));
    }

    [Fact]
    public void FamilyInvariants_Hold()
    {
        Assert.All(Set.Where(i => i.Family == "credit-note"), i => Assert.True(i.Total < 0));
        Assert.All(Set.Where(i => i.Family == "due-before-invoice"), i => Assert.True(i.DueDate < i.InvoiceDate));
        Assert.All(Set.Where(i => i.Family == "many-tax-lines"), i => Assert.True(i.TaxLines.Count >= 5));
        Assert.All(Set.Where(i => i.Family == "discount"), i => Assert.True(i.Discount > 0));
        Assert.All(Set.Where(i => i.Family == "multi-currency"), i => Assert.True(i.Money.Code is not ("USD" or "EUR")));
        Assert.All(Set.Where(i => i.Family == "multi-page"), i => Assert.True(i.Lines.Count > 40));
    }

    [Fact]
    public void TextLayer_ContainsEveryGoldenValueAsPrinted() =>
        Assert.All(Set, inv =>
        {
            var text = InvoiceRenderer.Text(inv);
            var dto = inv.ToDto();
            Assert.Contains(dto.Vendor!.Name!, text, StringComparison.Ordinal);
            Assert.Contains(dto.Customer!.Name!, text, StringComparison.Ordinal);
            Assert.Contains(dto.InvoiceNumber!, text, StringComparison.Ordinal);
            Assert.Contains(inv.Money.Format(dto.Total!.Value), text, StringComparison.Ordinal);
            Assert.Contains(inv.Money.Format(dto.Subtotal!.Value), text, StringComparison.Ordinal);
            Assert.All(dto.LineItems!, l => Assert.Contains(l.Description, text, StringComparison.Ordinal));
            Assert.Equal(dto.Subtotal!.Value - (dto.Discount ?? 0) + (dto.Tax ?? 0), dto.Total!.Value);
        });

    [Fact]
    public void MultiPage_RendersMoreThanOnePage() =>
        Assert.True(InvoiceRenderer.RenderPng(Set.First(i => i.Family == "multi-page")).Count > 1);

    private static string Snapshot(SyntheticInvoice inv) =>
        JsonSerializer.Serialize(inv.ToDto(), GoldenSet.JsonOptions) + "\n" + InvoiceRenderer.Text(inv);
}
