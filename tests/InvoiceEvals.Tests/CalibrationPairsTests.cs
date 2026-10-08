using InvoiceEvals.Evaluation;

namespace InvoiceEvals.Tests;

public sealed class CalibrationPairsTests
{
    private static readonly CalibrationPairs.Source[] Sources =
    [
        .. new[] { "Mclean-Cochran", "Acme International Ltd", "Smith Brothers Manufacturing", "Northwind Traders", "Blue Mountain Services", "Contoso Group" }
            .Select((n, i) => new CalibrationPairs.Source($"fatura/T{i}_I0", "vendor_name", n, $"INVOICE\n{n}\nTOTAL 1.00 USD\n")),
        .. new[] { "Natasha Torres", "Jane Doe", "Kevin Leach", "Maria Garcia" }
            .Select((n, i) => new CalibrationPairs.Source($"fatura/T{i}_I1", "customer_name", n, $"Bill to:\n{n}\n")),
    ];

    private static readonly CalibrationPairs.RealMismatch[] Real =
    [
        new("fatura/T1_I0", "vendor_name", "Acme International Ltd", "Acme Intl.", "Acme International Ltd\n", "mini-plain"),
        new("fatura/T1_I0", "vendor_name", "Acme International Ltd", "Acme Intl.", "Acme International Ltd\n", "strong-plain"), // duplicate pair
        new("fatura/T2_I0", "vendor_name", "Smith Brothers Manufacturing", "SMITH BROTHERS MANUFACTURING INC", "", "mini-plain"), // strict match: not gray zone
    ];

    [Fact]
    public void Generate_RealFirstDeduped_ThenSyntheticToTarget()
    {
        var pairs = CalibrationPairs.Generate(Real, Sources, target: 20, seed: 42);

        Assert.Equal(20, pairs.Count);
        var real = Assert.Single(pairs, p => p.Origin.StartsWith("run:", StringComparison.Ordinal));
        Assert.Equal("run:mini-plain,strong-plain", real.Origin);
        Assert.Equal(19, pairs.Count(p => p.Origin.StartsWith("synthetic:", StringComparison.Ordinal)));
        Assert.All(pairs, p => Assert.Null(p.HumanLabel));
    }

    [Fact]
    public void Generate_EveryPairIsGrayZone_Unique_AndHasExcerpt()
    {
        var pairs = CalibrationPairs.Generate(Real, Sources, target: 40, seed: 42);

        Assert.All(pairs, p => Assert.True(CalibrationPairs.IsGrayZone(p.Golden, p.Predicted), $"{p.Golden} / {p.Predicted}"));
        Assert.Equal(pairs.Count, pairs.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(pairs, p => Assert.Contains(p.Golden, p.Excerpt, StringComparison.Ordinal));
        Assert.True(pairs.Select(p => p.Origin).Distinct().Count() >= 6, "every perturbation kind is represented");
    }

    [Fact]
    public void Generate_IsDeterministic_AndSeedSensitive()
    {
        var a = CalibrationPairs.Generate(Real, Sources, 30, 42);
        var b = CalibrationPairs.Generate(Real, Sources.Reverse(), 30, 42); // Input order does not matter.
        var c = CalibrationPairs.Generate(Real, Sources, 30, 43);

        Assert.Equal(a, b);
        Assert.NotEqual(a.Select(p => p.Id), c.Select(p => p.Id));
    }

    [Fact]
    public void KeepLabels_CarriesLabelsById()
    {
        var pairs = CalibrationPairs.Generate(Real, Sources, 10, 42);
        var labelled = new[] { pairs[0] with { HumanLabel = true }, pairs[1] with { HumanLabel = false } };

        var merged = CalibrationPairs.KeepLabels(pairs, labelled);

        Assert.Equal([true, false], merged.Take(2).Select(p => p.HumanLabel));
        Assert.All(merged.Skip(2), p => Assert.Null(p.HumanLabel));
    }

    [Fact]
    public async Task Jsonl_RoundTrips_WithSnakeCaseAndEmptyLabel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pairs-{Guid.NewGuid():N}.jsonl");
        try
        {
            var pairs = CalibrationPairs.Generate(Real, Sources, 5, 42);
            await CalibrationPairs.WriteAsync(path, pairs, TestContext.Current.CancellationToken);

            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("\"human_label\":null", text, StringComparison.Ordinal);
            Assert.Contains("\"doc_id\":", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', text);
            Assert.Equal(pairs, await CalibrationPairs.ReadAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
