using System.ComponentModel;
using System.Text.RegularExpressions;

using Microsoft.Extensions.AI;

namespace InvoiceEvals.Agent;

/// <summary>Result of <see cref="InvoiceTools.ValidateTotals"/>. <c>Consistent</c> is null when there is nothing to check.</summary>
/// <param name="Difference">total − (subtotal − discount + tax); null when not computable.</param>
/// <param name="ReconciledTotal">subtotal − discount + tax; null when not computable.</param>
public sealed record TotalsCheck(bool? Consistent, decimal? Difference, decimal? ReconciledTotal, string Reason);

/// <summary>Result of <see cref="InvoiceTools.NormalizeCurrency"/>. <c>Code</c> is null when the text is ambiguous or unknown.</summary>
public sealed record CurrencyCode(string? Code, string Reason);

/// <summary>
/// The agent's two tools. Both are pure functions of their arguments, so a cached replay of the agent loop produces
/// the same tool results and therefore the same follow-up requests. They report; they never change an extracted value.
/// </summary>
public static partial class InvoiceTools
{
    public const string ValidateTotalsName = "validate_totals";
    public const string NormalizeCurrencyName = "normalize_currency";

    /// <summary>Same tolerance as the amount comparison in docs/metrics.md: 0.01 inclusive.</summary>
    public const decimal Tolerance = 0.01m;

    [Description("Checks whether the printed amounts reconcile: subtotal − discount + tax = total, within 0.01. Reports the difference; it does not correct anything. Pass the amounts exactly as extracted; discount is a positive number; omit amounts that are not printed.")]
    public static TotalsCheck ValidateTotals(
        [Description("Subtotal as printed, or null if not printed.")] decimal? subtotal = null,
        [Description("Discount as a positive number, or null if not printed.")] decimal? discount = null,
        [Description("Sum of all printed tax lines, or null if not printed.")] decimal? tax = null,
        [Description("Total (amount payable) as printed, or null if not printed.")] decimal? total = null)
    {
        if (subtotal is not { } s || total is not { } t)
            return new TotalsCheck(null, null, null, "Subtotal or total is not printed; nothing to check.");
        var reconciled = s - (discount ?? 0) + (tax ?? 0);
        var difference = t - reconciled;
        var consistent = Math.Abs(difference) <= Tolerance;
        return new TotalsCheck(consistent, difference, reconciled, consistent
            ? "Printed amounts reconcile."
            : $"Printed total {t} differs from subtotal − discount + tax = {reconciled} by {difference}.");
    }

    // Symbols and names with exactly one ISO 4217 reading. Bare "$", "¥", "kr" and "Rs" are deliberately absent: several currencies use them.
    private static readonly Dictionary<string, string> Unambiguous = new(StringComparer.Ordinal)
    {
        ["€"] = "EUR", ["£"] = "GBP", ["₹"] = "INR", ["₩"] = "KRW", ["₽"] = "RUB", ["₺"] = "TRY", ["₪"] = "ILS", ["₫"] = "VND", ["฿"] = "THB", ["₱"] = "PHP", ["zł"] = "PLN",
        ["us$"] = "USD", ["u.s.$"] = "USD", ["ca$"] = "CAD", ["c$"] = "CAD", ["can$"] = "CAD", ["a$"] = "AUD", ["au$"] = "AUD", ["nz$"] = "NZD", ["hk$"] = "HKD", ["s$"] = "SGD", ["mx$"] = "MXN", ["r$"] = "BRL",
        ["chf"] = "CHF", ["fr."] = "CHF", ["sfr"] = "CHF",
        ["us dollar"] = "USD", ["u.s. dollar"] = "USD", ["american dollar"] = "USD", ["canadian dollar"] = "CAD", ["australian dollar"] = "AUD", ["new zealand dollar"] = "NZD",
        ["hong kong dollar"] = "HKD", ["singapore dollar"] = "SGD", ["euro"] = "EUR", ["pound sterling"] = "GBP", ["british pound"] = "GBP", ["sterling"] = "GBP",
        ["japanese yen"] = "JPY", ["yen"] = "JPY", ["chinese yuan"] = "CNY", ["renminbi"] = "CNY", ["yuan"] = "CNY", ["swiss franc"] = "CHF", ["indian rupee"] = "INR", ["swedish krona"] = "SEK",
        ["norwegian krone"] = "NOK", ["danish krone"] = "DKK", ["polish zloty"] = "PLN", ["mexican peso"] = "MXN", ["brazilian real"] = "BRL", ["south african rand"] = "ZAR",
    };

    private static readonly Dictionary<string, string> Ambiguous = new(StringComparer.Ordinal)
    {
        ["$"] = "'$' alone is used by USD, CAD, AUD, NZD, SGD, HKD, MXN and others.",
        ["dollar"] = "'dollar' without a country names several currencies.",
        ["¥"] = "'¥' is used by both JPY and CNY.",
        ["kr"] = "'kr' is used by SEK, NOK, DKK and ISK.",
        ["krona"] = "'krona' is used by SEK and ISK.",
        ["krone"] = "'krone' is used by NOK and DKK.",
        ["rs"] = "'Rs' is used by INR, PKR, LKR and NPR.",
        ["rs."] = "'Rs' is used by INR, PKR, LKR and NPR.",
        ["rupee"] = "'rupee' is used by INR, PKR, LKR and NPR.",
        ["pound"] = "'pound' alone is used by GBP, EGP and others.",
        ["franc"] = "'franc' alone is used by CHF, XOF, XAF and others.",
        ["peso"] = "'peso' alone is used by MXN, ARS, COP, CLP, PHP and others.",
    };

    // Codes in active use (ISO 4217, list one). Not exhaustive on purpose: a code outside it is reported as unknown, not guessed.
    private static readonly HashSet<string> IsoCodes = new(StringComparer.Ordinal)
    {
        "AED", "ARS", "AUD", "BGN", "BRL", "CAD", "CHF", "CLP", "CNY", "COP", "CZK", "DKK", "EGP", "EUR", "GBP", "HKD", "HUF", "IDR", "ILS", "INR",
        "ISK", "JPY", "KRW", "MXN", "MYR", "NOK", "NZD", "PHP", "PKR", "PLN", "RON", "RUB", "SAR", "SEK", "SGD", "THB", "TRY", "TWD", "UAH", "USD",
        "VND", "XAF", "XOF", "ZAR",
    };

    [Description("Maps a currency symbol, code or name as printed (for example '€', 'EUR', 'US Dollar', 'CA$') to its ISO 4217 code. Returns a null code with a reason when the text is ambiguous (for example '$' alone) or unknown.")]
    public static CurrencyCode NormalizeCurrency([Description("The currency text exactly as printed on the invoice.")] string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return new CurrencyCode(null, "Empty text.");

        var upper = trimmed.ToUpperInvariant();
        if (upper.Length == 3 && IsoCodes.Contains(upper)) return new CurrencyCode(upper, $"'{trimmed}' is an ISO 4217 code.");

        var key = Whitespace().Replace(trimmed.ToLowerInvariant(), " ");
        var singular = key.EndsWith('s') && !key.EndsWith("ss", StringComparison.Ordinal) ? key[..^1] : key;
        foreach (var candidate in new[] { key, singular })
        {
            if (Unambiguous.TryGetValue(candidate, out var code)) return new CurrencyCode(code, $"'{trimmed}' denotes {code}.");
            if (Ambiguous.TryGetValue(candidate, out var reason)) return new CurrencyCode(null, $"Ambiguous: {reason}");
        }
        if (upper.Length == 3 && upper.All(char.IsAsciiLetterUpper))
            return new CurrencyCode(null, $"'{trimmed}' looks like a code but is not in the supported ISO 4217 list.");
        return new CurrencyCode(null, $"Unknown currency text '{trimmed}'.");
    }

    /// <summary>The tool definitions the agent receives and the quality evaluators are given.</summary>
    public static IReadOnlyList<AITool> All { get; } =
    [
        AIFunctionFactory.Create(ValidateTotals, ValidateTotalsName),
        AIFunctionFactory.Create(NormalizeCurrency, NormalizeCurrencyName),
    ];

    /// <summary>
    /// Hash of the tool names, descriptions and JSON schemas. The response cache key does not include
    /// <see cref="ChatOptions.Tools"/>, so agent runs pass this as a caching key: editing a tool definition is a cache miss.
    /// </summary>
    public static string Fingerprint(IEnumerable<AITool> tools)
    {
        var text = string.Join('\n', tools.OfType<AIFunctionDeclaration>().Select(t => $"{t.Name}\n{t.Description}\n{t.JsonSchema.GetRawText()}"));
        return $"tools:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16]}";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
