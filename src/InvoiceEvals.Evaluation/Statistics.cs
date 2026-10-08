namespace InvoiceEvals.Evaluation;

/// <summary>
/// SplitMix64 (Steele, Lea, Flood 2014): a tiny seeded generator whose output is fixed by the algorithm, unlike
/// <see cref="Random"/>, whose sequence may change between runtimes. Used wherever results must be reproducible.
/// </summary>
public struct SplitMix64(ulong seed)
{
    private ulong state = seed;

    public ulong Next()
    {
        var z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, n) by 64-bit multiply-high (Lemire); bias is at most n / 2^64.</summary>
    public int NextInt(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(n);
        return (int)Math.BigMul(Next(), (ulong)n, out _);
    }
}

/// <param name="N">Paired observations.</param>
/// <param name="Mean">Observed mean delta.</param>
/// <param name="Low">Lower bound of the percentile interval.</param>
/// <param name="High">Upper bound of the percentile interval.</param>
public sealed record BootstrapInterval(int N, double Mean, double Low, double High)
{
    /// <summary>The interval excludes 0.</summary>
    public bool Significant => Low > 0 || High < 0;
}

/// <summary>Paired bootstrap of a mean: resample per-document deltas with replacement, percentile interval.</summary>
public static class PairedBootstrap
{
    public const int DefaultResamples = 10_000;
    public const ulong DefaultSeed = 42;

    /// <summary>
    /// Mean of <paramref name="deltas"/> with a percentile confidence interval: <paramref name="resamples"/> means
    /// of n draws with replacement, sorted, bounds at the nearest ranks of α/2 and 1 − α/2.
    /// </summary>
    public static BootstrapInterval MeanInterval(
        ReadOnlySpan<double> deltas, int resamples = DefaultResamples, ulong seed = DefaultSeed, double confidence = 0.95)
    {
        if (deltas.IsEmpty) throw new ArgumentException("At least one delta is required.", nameof(deltas));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resamples);
        if (confidence is <= 0 or >= 1) throw new ArgumentOutOfRangeException(nameof(confidence), "Must be in (0, 1).");

        var n = deltas.Length;
        var rng = new SplitMix64(seed);
        var means = new double[resamples];
        for (var r = 0; r < resamples; r++)
        {
            var sum = 0.0;
            for (var i = 0; i < n; i++) sum += deltas[rng.NextInt(n)];
            means[r] = sum / n;
        }
        Array.Sort(means);

        var alpha = 1 - confidence;
        return new BootstrapInterval(n, Mean(deltas), means[NearestRank(alpha / 2, resamples)], means[NearestRank(1 - (alpha / 2), resamples)]);
    }

    /// <summary>Zero-based index of the nearest-rank percentile p of a sorted sample of size n.</summary>
    public static int NearestRank(double p, int n) => Math.Clamp((int)Math.Ceiling(Math.Round(p * n, 9)) - 1, 0, n - 1);

    private static double Mean(ReadOnlySpan<double> values)
    {
        var sum = 0.0;
        foreach (var v in values) sum += v;
        return sum / values.Length;
    }
}

/// <summary>Agreement of two binary raters with chance correction.</summary>
/// <param name="BothTrue">Human true, judge true.</param>
/// <param name="HumanOnly">Human true, judge false.</param>
/// <param name="JudgeOnly">Human false, judge true.</param>
/// <param name="BothFalse">Human false, judge false.</param>
public sealed record CohenKappa(int BothTrue, int HumanOnly, int JudgeOnly, int BothFalse)
{
    public int N => BothTrue + HumanOnly + JudgeOnly + BothFalse;

    /// <summary>Observed agreement p_o.</summary>
    public double Observed => (double)(BothTrue + BothFalse) / N;

    /// <summary>Agreement expected by chance from the marginals, p_e.</summary>
    public double Expected
    {
        get
        {
            double n = N;
            return ((BothTrue + HumanOnly) / n * ((BothTrue + JudgeOnly) / n)) + ((JudgeOnly + BothFalse) / n * ((HumanOnly + BothFalse) / n));
        }
    }

    /// <summary>(p_o − p_e) / (1 − p_e); NaN when undefined (both raters use a single label, p_e = 1).</summary>
    public double Kappa => Expected >= 1 ? double.NaN : (Observed - Expected) / (1 - Expected);

    public static CohenKappa From(IEnumerable<(bool Human, bool Judge)> pairs)
    {
        int tt = 0, tf = 0, ft = 0, ff = 0;
        foreach (var (human, judge) in pairs)
        {
            switch (human, judge)
            {
                case (true, true): tt++; break;
                case (true, false): tf++; break;
                case (false, true): ft++; break;
                default: ff++; break;
            }
        }
        if (tt + tf + ft + ff == 0) throw new ArgumentException("At least one pair is required.", nameof(pairs));
        return new CohenKappa(tt, tf, ft, ff);
    }
}
