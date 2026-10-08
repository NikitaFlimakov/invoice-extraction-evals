using InvoiceEvals.Evaluation;

namespace InvoiceEvals.Tests;

public sealed class StatisticsTests
{
    // ---------- PairedBootstrap ----------

    // Hand-computed: with deltas {0, 1}, a resample mean is 0 (p = 1/4), 0.5 (1/2) or 1 (1/4). With 10,000 resamples
    // about 2,500 means are 0 and 2,500 are 1, so the 2.5th percentile (rank 250) is 0 and the 97.5th (rank 9,750) is 1.
    [Fact]
    public void Bootstrap_TwoPoints_HandComputedInterval()
    {
        var ci = PairedBootstrap.MeanInterval([0.0, 1.0]);

        Assert.Equal(2, ci.N);
        Assert.Equal(0.5, ci.Mean);
        Assert.Equal(0.0, ci.Low);
        Assert.Equal(1.0, ci.High);
        Assert.False(ci.Significant);
    }

    // Same distribution shifted by +1: {1, 2} gives [1, 2], which excludes 0.
    [Fact]
    public void Bootstrap_ShiftedTwoPoints_IsSignificant()
    {
        var ci = PairedBootstrap.MeanInterval([1.0, 2.0]);

        Assert.Equal((1.5, 1.0, 2.0), (ci.Mean, ci.Low, ci.High));
        Assert.True(ci.Significant);
    }

    [Theory]
    [InlineData(0.0, false)]
    [InlineData(-0.25, true)]
    public void Bootstrap_ConstantDeltas_DegenerateInterval(double delta, bool significant)
    {
        var ci = PairedBootstrap.MeanInterval(Enumerable.Repeat(delta, 50).ToArray());

        Assert.Equal((delta, delta, delta), (ci.Mean, ci.Low, ci.High));
        Assert.Equal(significant, ci.Significant);
    }

    // Hand-computed for {0,0,0,1}: P(mean = 0) = (3/4)^4 = 0.316 > 0.025, so Low = 0. P(mean ≥ 0.75) = 13/256 = 0.051
    // > 0.025 but P(mean = 1) = 1/256 < 0.025, so High = 0.75.
    [Fact]
    public void Bootstrap_OneInFour_HandComputedInterval()
    {
        var ci = PairedBootstrap.MeanInterval([0.0, 0.0, 0.0, 1.0]);

        Assert.Equal((0.25, 0.0, 0.75), (ci.Mean, ci.Low, ci.High));
    }

    [Fact]
    public void Bootstrap_SameSeed_SameInterval_DifferentSeed_StillCloseOnLargeSample()
    {
        var rng = new SplitMix64(7);
        var deltas = Enumerable.Range(0, 180).Select(_ => (rng.NextInt(3) - 1) * 1.0).ToArray();

        var a = PairedBootstrap.MeanInterval(deltas, seed: 42);
        var b = PairedBootstrap.MeanInterval(deltas, seed: 42);
        var c = PairedBootstrap.MeanInterval(deltas, seed: 43);

        Assert.Equal(a, b);
        Assert.InRange(Math.Abs(a.Low - c.Low), 0, 0.02);
        Assert.True(a.Low <= a.Mean && a.Mean <= a.High);
    }

    [Fact]
    public void Bootstrap_Empty_Throws() =>
        Assert.Throws<ArgumentException>(() => PairedBootstrap.MeanInterval([]));

    [Theory]
    [InlineData(0.025, 10_000, 249)] // 0.025 * 10,000 is 250.00000000000003 in floating point; must not become rank 251.
    [InlineData(0.975, 10_000, 9_749)]
    [InlineData(0.5, 5, 2)]
    [InlineData(0.0, 5, 0)]
    [InlineData(1.0, 5, 4)]
    public void NearestRank_IsZeroBasedAndRobustToRounding(double p, int n, int expected) =>
        Assert.Equal(expected, PairedBootstrap.NearestRank(p, n));

    [Fact]
    public void SplitMix64_NextInt_IsInRangeAndCoversIt()
    {
        var rng = new SplitMix64(1);
        var seen = new int[7];
        for (var i = 0; i < 7_000; i++) seen[rng.NextInt(7)]++;

        Assert.All(seen, count => Assert.InRange(count, 850, 1150));
    }

    // ---------- CohenKappa ----------

    // Hand-computed: TP 4, FN 1, FP 1, TN 4. p_o = 0.8; both raters say "true" 5/10 times, so p_e = 0.5; κ = 0.6.
    [Fact]
    public void Kappa_HandComputed()
    {
        var k = CohenKappa.From([.. Pairs(true, true, 4), .. Pairs(true, false, 1), .. Pairs(false, true, 1), .. Pairs(false, false, 4)]);

        Assert.Equal((4, 1, 1, 4), (k.BothTrue, k.HumanOnly, k.JudgeOnly, k.BothFalse));
        Assert.Equal(0.8, k.Observed, 12);
        Assert.Equal(0.5, k.Expected, 12);
        Assert.Equal(0.6, k.Kappa, 12);
    }

    // Unbalanced marginals: TP 45, FN 5, FP 15, TN 35. p_o = 0.8; p_e = 0.5*0.6 + 0.5*0.4 = 0.5; κ = 0.6.
    [Fact]
    public void Kappa_UnbalancedMarginals()
    {
        var k = CohenKappa.From([.. Pairs(true, true, 45), .. Pairs(true, false, 5), .. Pairs(false, true, 15), .. Pairs(false, false, 35)]);

        Assert.Equal(0.6, k.Kappa, 12);
    }

    [Fact]
    public void Kappa_PerfectAgreement_Is1_SingleLabel_IsUndefined()
    {
        Assert.Equal(1.0, CohenKappa.From([.. Pairs(true, true, 3), .. Pairs(false, false, 3)]).Kappa, 12);
        Assert.True(double.IsNaN(CohenKappa.From(Pairs(true, true, 5)).Kappa));
    }

    private static IEnumerable<(bool, bool)> Pairs(bool human, bool judge, int count) => Enumerable.Repeat((human, judge), count);
}
