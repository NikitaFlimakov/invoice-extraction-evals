using System.Security.Cryptography;
using System.Text;

namespace InvoiceEvals.Core;

/// <summary>
/// Deterministic stratified sampling. Items are ranked by SHA-256 of "seed:key" rather than System.Random,
/// so the sample is stable across .NET versions and independent of input order.
/// </summary>
public static class StratifiedSampler
{
    /// <summary>
    /// Takes <paramref name="count"/> items split evenly across strata; the remainder goes to strata ranked first by hash.
    /// </summary>
    public static IReadOnlyList<T> Sample<T>(IEnumerable<T> items, Func<T, string> key, Func<T, string> stratum, int count, int seed)
    {
        var strata = items.GroupBy(stratum).OrderBy(g => Rank(seed, g.Key), StringComparer.Ordinal).ToList();
        if (strata.Count == 0 || count <= 0) return [];
        var (perStratum, remainder) = Math.DivRem(count, strata.Count);

        return [.. strata.SelectMany((g, i) => g
            .OrderBy(x => Rank(seed, key(x)), StringComparer.Ordinal)
            .Take(perStratum + (i < remainder ? 1 : 0)))];
    }

    private static string Rank(int seed, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{key}")));
}
