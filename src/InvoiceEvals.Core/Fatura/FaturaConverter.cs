using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvoiceEvals.Core.Fatura;

/// <summary>
/// Converts a FATURA "Original_Format" annotation into an <see cref="InvoiceDto"/>.
/// Rules are documented in docs/annotation-guidelines.md; keep both in sync.
/// </summary>
public static partial class FaturaConverter
{
    private static readonly string[] AmountKeys = ["TOTAL", "SUB_TOTAL", "AMOUNT_DUE"];

    /// <exception cref="FormatException">The annotation is defective or has an unexpected shape.</exception>
    public static InvoiceDto Convert(string annotationJson)
    {
        using var doc = JsonDocument.Parse(annotationJson);
        var root = doc.RootElement;

        var date = Text(root, "DATE");
        if (date is not null && date.TrimStart().StartsWith("Due", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"DATE is labelled with a due date: '{date}'.");

        var taxLines = root.EnumerateObject()
            .Where(p => p.Name == "TAX" || p.Name.StartsWith("GST(", StringComparison.Ordinal))
            .Select(p => Amount(Text(root, p.Name), p.Name))
            .OfType<decimal>()
            .ToList();

        var currencies = AmountKeys
            .Select(k => Currency(Text(root, k)))
            .OfType<string>()
            .Distinct()
            .ToList();
        if (currencies.Count > 1)
            throw new FormatException($"Conflicting currencies: {string.Join(", ", currencies)}.");

        var sellerName = Text(root, "SELLER_NAME")?.Trim();
        var sellerAddress = Text(root, "SELLER_ADDRESS") is { } sa ? ParseAddress(AddressLabel().Replace(sa, ""), "SELLER_ADDRESS") : null;
        var customerBlock = Text(root, "BILL_TO") ?? Text(root, "BUYER");

        return new InvoiceDto(
            Vendor: sellerName is null && sellerAddress is null ? null : new Party(sellerName, sellerAddress),
            Customer: customerBlock is null ? null : ParseParty(customerBlock),
            InvoiceNumber: Text(root, "NUMBER")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1],
            InvoiceDate: Date(date, "DATE"),
            DueDate: Date(Text(root, "DUE_DATE"), "DUE_DATE"),
            Currency: currencies.SingleOrDefault(),
            Subtotal: Amount(Text(root, "SUB_TOTAL"), "SUB_TOTAL"),
            Discount: Amount(Text(root, "DISCOUNT"), "DISCOUNT"),
            Tax: taxLines.Count == 0 ? null : taxLines.Sum(),
            Total: Amount(Text(root, "TOTAL") ?? Text(root, "AMOUNT_DUE"), "TOTAL"),
            LineItems: null); // FATURA annotates the table only as a bounding box.
    }

    private static string? Text(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("text", out var t)
            ? t.GetString()
            : null;

    private static decimal? Amount(string? text, string key)
    {
        if (text is null) return null;
        var matches = Number().Matches(text);
        if (matches.Count == 0) throw new FormatException($"{key}: no amount in '{text}'.");
        return decimal.Parse(matches[^1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private static string? Currency(string? text) =>
        text is null ? null : TrailingCurrency().Match(text) switch
        {
            { Success: false } => null,
            { Value: var v } => v.Trim() switch
            {
                "$" or "USD" => "USD",
                "EUR" => "EUR",
                var other => throw new FormatException($"Unknown currency '{other}'."),
            },
        };

    private static DateOnly? Date(string? text, string key)
    {
        if (text is null) return null;
        var m = DatePattern().Match(text);
        if (!m.Success) throw new FormatException($"{key}: no date in '{text}'.");
        return DateOnly.ParseExact(m.Value, "d-MMM-yyyy", CultureInfo.InvariantCulture);
    }

    private static Party ParseParty(string block)
    {
        var lines = Lines(PartyLabel().Replace(block, ""));
        if (lines.Count == 0) throw new FormatException($"Empty party block '{block}'.");
        return new Party(lines[0], ParseAddress(string.Join('\n', lines.Skip(1)), "BUYER"));
    }

    /// <summary>Parses "street lines\nCity, ST 12345 US", ignoring trailing contact lines (Tel/Email/Site).</summary>
    private static Address ParseAddress(string text, string key)
    {
        var lines = Lines(text);
        var cityIndex = lines.FindIndex(l => CityLine().IsMatch(l));
        if (cityIndex < 1) throw new FormatException($"{key}: no street + city line in '{text}'.");
        var m = CityLine().Match(lines[cityIndex]);
        return new Address(
            Street: string.Join(", ", lines.Take(cityIndex)),
            City: m.Groups["city"].Value,
            Region: m.Groups["region"].Value,
            PostalCode: m.Groups["zip"].Value,
            Country: m.Groups["country"].Value);
    }

    private static List<string> Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    [GeneratedRegex(@"\d+(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"(?:\$|USD|EUR)\s*$")]
    private static partial Regex TrailingCurrency();

    [GeneratedRegex(@"\b\d{1,2}-[A-Za-z]{3}-\d{4}\b")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^\s*(?:Bill to|Buyer|BILL_TO)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PartyLabel();

    [GeneratedRegex(@"^\s*Address\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AddressLabel();

    [GeneratedRegex(@"^(?<city>.+?),?\s+(?<region>[A-Z]{2})\s+(?<zip>\d{5})\s+(?<country>[A-Z]{2})$")]
    private static partial Regex CityLine();
}
