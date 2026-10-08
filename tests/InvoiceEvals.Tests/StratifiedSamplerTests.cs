using InvoiceEvals.Core;

namespace InvoiceEvals.Tests;

public sealed class StratifiedSamplerTests
{
    // 5 layouts x 20 documents.
    private static readonly (string Id, string Layout)[] Items =
        [.. Enumerable.Range(0, 100).Select(i => ($"Template{i % 5}_Instance{i / 5}", $"Template{i % 5}"))];

    private static IReadOnlyList<(string Id, string Layout)> Sample(IEnumerable<(string Id, string Layout)> items, int count, int seed) =>
        StratifiedSampler.Sample(items, x => x.Id, x => x.Layout, count, seed);

    [Fact]
    public void SameSeed_SameSample_RegardlessOfInputOrder()
    {
        var a = Sample(Items, 12, seed: 42).Select(x => x.Id).Order(StringComparer.Ordinal);
        var b = Sample(Items.Reverse(), 12, seed: 42).Select(x => x.Id).Order(StringComparer.Ordinal);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeed_DifferentSample() =>
        Assert.NotEqual(
            Sample(Items, 12, seed: 1).Select(x => x.Id).Order(StringComparer.Ordinal),
            Sample(Items, 12, seed: 2).Select(x => x.Id).Order(StringComparer.Ordinal));

    [Fact]
    public void SplitsEvenly_RemainderGoesToOneExtraPerLayout()
    {
        var perLayout = Sample(Items, 12, seed: 42).CountBy(x => x.Layout).Select(kv => kv.Value).Order();

        Assert.Equal([2, 2, 2, 3, 3], perLayout);
    }

    [Fact]
    public void NoDuplicates() =>
        Assert.Distinct(Sample(Items, 50, seed: 42).Select(x => x.Id));

    [Fact]
    public void HoldOutStrata_RemovesWholeLayouts_Deterministically()
    {
        var (heldOut, remaining) = StratifiedSampler.HoldOutStrata(Items.Reverse(), x => x.Layout, 2, seed: 42);
        var again = StratifiedSampler.HoldOutStrata(Items, x => x.Layout, 2, seed: 42).HeldOut;

        Assert.Equal(2, heldOut.Select(x => x.Layout).Distinct().Count());
        Assert.Equal(40, heldOut.Count);
        Assert.Empty(remaining.Select(x => x.Layout).Intersect(heldOut.Select(x => x.Layout)));
        Assert.Equal(heldOut.Select(x => x.Id).Order(StringComparer.Ordinal), again.Select(x => x.Id).Order(StringComparer.Ordinal));
    }
}
