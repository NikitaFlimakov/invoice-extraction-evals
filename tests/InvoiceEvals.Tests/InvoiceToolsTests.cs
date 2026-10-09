using InvoiceEvals.Agent;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Tests;

public sealed class InvoiceToolsTests
{
    [Fact]
    public void ValidateTotals_ReconcilingAmounts_AreConsistent()
    {
        var check = InvoiceTools.ValidateTotals(100.00m, 10.00m, 19.00m, 109.00m);

        Assert.True(check.Consistent);
        Assert.Equal(0m, check.Difference);
        Assert.Equal(109.00m, check.ReconciledTotal);
    }

    [Fact]
    public void ValidateTotals_DifferenceOfExactlyOneCent_IsConsistent()
    {
        Assert.True(InvoiceTools.ValidateTotals(100.00m, null, null, 100.01m).Consistent);
        Assert.False(InvoiceTools.ValidateTotals(100.00m, null, null, 100.02m).Consistent);
    }

    [Fact]
    public void ValidateTotals_InconsistentAmounts_ReportTheSignedDifference()
    {
        var check = InvoiceTools.ValidateTotals(800.00m, 30.67m, null, 800.81m);

        Assert.False(check.Consistent);
        Assert.Equal(31.48m, check.Difference);
        Assert.Equal(769.33m, check.ReconciledTotal);
        Assert.Contains("769.33", check.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 10.0)]
    [InlineData(10.0, null)]
    public void ValidateTotals_SubtotalOrTotalMissing_HasNothingToCheck(double? subtotal, double? total)
    {
        var check = InvoiceTools.ValidateTotals((decimal?)subtotal, null, null, (decimal?)total);

        Assert.Null(check.Consistent);
        Assert.Null(check.Difference);
    }

    [Theory]
    [InlineData("€", "EUR")]
    [InlineData("EUR", "EUR")]
    [InlineData(" usd ", "USD")]
    [InlineData("US Dollar", "USD")]
    [InlineData("US dollars", "USD")]
    [InlineData("US$", "USD")]
    [InlineData("CA$", "CAD")]
    [InlineData("£", "GBP")]
    [InlineData("Euro", "EUR")]
    [InlineData("Japanese yen", "JPY")]
    [InlineData("CHF", "CHF")]
    public void NormalizeCurrency_Unambiguous_ReturnsIsoCode(string text, string expected) =>
        Assert.Equal(expected, InvoiceTools.NormalizeCurrency(text).Code);

    [Theory]
    [InlineData("$")]
    [InlineData("dollars")]
    [InlineData("¥")]
    [InlineData("kr")]
    [InlineData("Rs.")]
    public void NormalizeCurrency_Ambiguous_ReturnsNullWithAReason(string text)
    {
        var result = InvoiceTools.NormalizeCurrency(text);

        Assert.Null(result.Code);
        Assert.StartsWith("Ambiguous", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("XYZ")]
    [InlineData("doubloons")]
    public void NormalizeCurrency_Unknown_ReturnsNull(string text) => Assert.Null(InvoiceTools.NormalizeCurrency(text).Code);

    [Fact]
    public async Task AsAIFunction_ValidateTotals_AcceptsOmittedAmounts()
    {
        var tool = (AIFunction)InvoiceTools.All.Single(t => t.Name == InvoiceTools.ValidateTotalsName);

        var result = await tool.InvokeAsync(new AIFunctionArguments { ["subtotal"] = 100m, ["total"] = 100m }, TestContext.Current.CancellationToken);

        Assert.Contains("\"consistent\":true", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_ChangesWhenADescriptionChanges()
    {
        var a = AIFunctionFactory.Create(InvoiceTools.NormalizeCurrency, InvoiceTools.NormalizeCurrencyName, "Maps currency text.");
        var b = AIFunctionFactory.Create(InvoiceTools.NormalizeCurrency, InvoiceTools.NormalizeCurrencyName, "Maps currency text to ISO 4217.");

        Assert.NotEqual(InvoiceTools.Fingerprint([a]), InvoiceTools.Fingerprint([b]));
        Assert.Equal(InvoiceTools.Fingerprint(InvoiceTools.All), InvoiceTools.Fingerprint(InvoiceTools.All));
    }
}
