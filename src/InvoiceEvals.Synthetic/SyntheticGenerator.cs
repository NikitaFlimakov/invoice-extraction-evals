using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using InvoiceEvals.Core;

namespace InvoiceEvals.Synthetic;

/// <summary>
/// Generates the synthetic edge-case set: 30 invoices across 7 families. Deterministic from the seed
/// (SHA-256 counter stream, not System.Random), so the same seed always yields the same invoices.
/// </summary>
public static class SyntheticGenerator
{
    public static readonly (string Family, int Count)[] Families =
    [
        ("multi-currency", 4),
        ("discount", 4),
        ("credit-note", 4),
        ("multi-page", 4),
        ("many-tax-lines", 5),
        ("due-before-invoice", 4),
        ("legal-suffix", 5),
    ];

    private static readonly string[] Vendors =
    [
        "Nordwind Logistik GmbH", "Acme Intl. Ltd.", "Brightwater Mfg. Co.", "O'Neill & Sons, LLC", "Kawasaki Seiki K.K.",
        "Hartmann Bros. AG", "Société Lumière S.A.", "Fratelli Rossi S.p.A.", "Atelier Dubois S.à r.l.", "Koala Supplies Pty Ltd",
        "Järvinen Tekniikka Oy", "Svensson & Berg AB", "Northgate Holdings PLC", "Van Dijk Techniek B.V.", "Müller Elektro GmbH & Co. KG",
        "Pacific Rim Trading Corp.", "Blue Harbor Consulting LLP", "Carter-Hughes Inc.", "Delta Freight Svcs. Ltd", "Greenfield Agri Co-op",
        "Larsen Byg ApS", "Iberia Textil S.L.", "Maple Leaf Printing Ltd.", "Summit Analytics, Inc.", "Old Mill Bakery",
        "Quantum Dynamics Intl. Corp.", "Rhein-Main Software GmbH", "St. Clair Hardware Co.", "Yamada Shoji Co., Ltd.", "Alpine Optik AG",
    ];

    private static readonly string[] Customers =
    [
        "Westbrook Medical Centre", "Jane Okafor", "Lindqvist Arkitekter AB", "Hotel Belvedere", "Riverside School District",
        "Marco Bianchi", "Fenwick & Partners", "Oakridge Dental Clinic", "Studio Kagami", "Priya Raman",
    ];

    private static readonly (string Street, string City, string? Region, string Postal, string Country)[] Addresses =
    [
        ("Hafenstraße 12", "Hamburg", null, "20457", "DE"), ("221 Baker Street", "London", null, "NW1 6XE", "GB"),
        ("8 Rue de Rivoli", "Paris", null, "75004", "FR"), ("1-2-3 Marunouchi", "Tokyo", null, "100-0005", "JP"),
        ("Bahnhofstrasse 45", "Zürich", null, "8001", "CH"), ("350 Bay Street", "Toronto", "ON", "M5H 2S6", "CA"),
        ("1600 Pine Avenue", "Denver", "CO", "80202", "US"), ("12 George Street", "Sydney", "NSW", "2000", "AU"),
        ("Drottninggatan 7", "Stockholm", null, "111 51", "SE"), ("Via Roma 18", "Milano", null, "20121", "IT"),
        ("Keizersgracht 100", "Amsterdam", null, "1015 CS", "NL"), ("Mannerheimintie 5", "Helsinki", null, "00100", "FI"),
    ];

    private static readonly string[] Nouns =
    [
        "steel bracket", "consulting hours", "LED panel", "shipping pallet", "software licence", "maintenance visit",
        "office chair", "printer toner", "network switch", "safety gloves", "training session", "hydraulic pump",
        "copper cable", "design review", "cloud storage", "packaging tape", "spare filter", "calibration service",
    ];

    private static readonly string[] Qualifiers = ["standard", "premium", "type A", "type B", "large", "small", "annual", "monthly", "M8", "2 m"];

    private static readonly Money Usd = new("USD", "$", 2, ",", SymbolAfter: false);
    private static readonly Money Eur = new("EUR", "EUR", 2, ",", SymbolAfter: true);

    private static readonly Money[] ForeignMoney =
    [
        new("GBP", "£", 2, ",", SymbolAfter: false),
        new("JPY", "JPY", 0, ",", SymbolAfter: true),
        new("CHF", "CHF ", 2, "'", SymbolAfter: false),
        new("CAD", "CA$", 2, ",", SymbolAfter: false),
    ];

    private static readonly Dictionary<string, decimal> EurRates = new(StringComparer.Ordinal) { ["GBP"] = 1.17m, ["JPY"] = 0.0062m, ["CHF"] = 1.05m, ["CAD"] = 0.68m };

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "d MMM yyyy", "MMMM d, yyyy", "dd.MM.yyyy"];

    public static IReadOnlyList<SyntheticInvoice> Generate(int seed)
    {
        var invoices = new List<SyntheticInvoice>();
        var vendorIndex = 0;
        foreach (var (family, count) in Families)
        {
            for (var i = 1; i <= count; i++)
            {
                var id = $"{family}-{i:00}";
                invoices.Add(Build(new HashRng(seed, id), id, family, i - 1, Vendors[vendorIndex++ % Vendors.Length]));
            }
        }
        return invoices;
    }

    private static SyntheticInvoice Build(HashRng rng, string id, string family, int indexInFamily, string vendorName)
    {
        var money = family == "multi-currency" ? ForeignMoney[indexInFamily % ForeignMoney.Length] : rng.Next(2) == 0 ? Usd : Eur;
        var unit = money.Decimals == 0 ? 100m : 1m; // JPY prices are in the hundreds-to-thousands.
        var sign = family == "credit-note" ? -1 : 1;

        var lineCount = family == "multi-page" ? 48 + rng.Next(14) : 2 + rng.Next(5);
        var lines = Enumerable.Range(0, lineCount).Select(_ =>
        {
            var qty = (decimal)(1 + rng.Next(9));
            var price = sign * Round((5 + rng.Next(49_500)) / 100m * unit, money.Decimals);
            return new LineItem($"{Capitalize(rng.Pick(Nouns))}, {rng.Pick(Qualifiers)}", qty, price, Round(qty * price, money.Decimals));
        }).ToList();
        var subtotal = lines.Sum(l => l.Amount!.Value);

        decimal? discount = null;
        string? discountLabel = null;
        if (family == "discount" || (family != "credit-note" && rng.Next(4) == 0))
        {
            var pct = 2 + rng.Next(14);
            discount = Round(subtotal * pct / 100m, money.Decimals);
            discountLabel = $"Discount ({pct}%)";
        }

        var taxBase = subtotal - (discount ?? 0);
        var taxRates = family == "many-tax-lines"
            ? new[] { ("GST", 5m), ("PST", 7m), ("City levy", 1.5m), ("Eco fee", 0.5m), ("Tourism tax", 2m), ("Excise", 3m) }.Take(5 + (indexInFamily % 2))
            : [(money.Code == "USD" ? "Sales tax" : "VAT", (decimal)(5 + rng.Next(20)))];
        var taxLines = taxRates.Select(t => ($"{t.Item1} {t.Item2}%", Round(taxBase * t.Item2 / 100m, money.Decimals))).ToList();

        var invoiceDate = new DateOnly(2021, 1, 1).AddDays(rng.Next(1400));
        var dueDate = family == "due-before-invoice" ? invoiceDate.AddDays(-(5 + rng.Next(60))) : invoiceDate.AddDays(14 + rng.Next(47));

        var terms = dueDate.DayNumber - invoiceDate.DayNumber;
        var notes = new List<string> { terms > 0 ? $"Payment terms: net {terms} days." : "Payment due on receipt.", $"IBAN DE{rng.Next(90) + 10} 3704 0044 0532 0130 {rng.Next(9000) + 1000}" };
        if (family == "multi-currency")
        {
            var rate = EurRates[money.Code] * (0.95m + (rng.Next(10) / 100m));
            var total = subtotal - (discount ?? 0) + taxLines.Sum(t => t.Item2);
            notes.Add($"For reference only: total in EUR {Round(total * rate, 2).ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture)} at {rate:0.0000} EUR/{money.Code}.");
        }
        if (family == "credit-note") notes.Add($"Credits invoice INV-{rng.Next(90000) + 10000} in full.");

        var customerName = rng.Pick(Customers);
        var shipTo = family == "legal-suffix" || rng.Next(3) == 0 ? Party(rng, rng.Pick(Customers.Where(c => c != customerName).ToArray())) : null;

        return new SyntheticInvoice(
            Id: $"synthetic/{id}",
            Family: family,
            Title: family == "credit-note" ? "CREDIT NOTE" : rng.Pick(["INVOICE", "TAX INVOICE", "Invoice"]),
            Vendor: Party(rng, vendorName),
            VendorTaxId: $"VAT ID {rng.Pick(["DE", "GB", "FR", "CH", "SE"])}{rng.Next(900_000_000) + 100_000_000}",
            Customer: Party(rng, customerName),
            ShipTo: shipTo,
            NumberLabel: family == "credit-note" ? "Credit note no." : rng.Pick(["Invoice No.", "Invoice #", "Number", "Inv. ref"]),
            Number: family == "credit-note" ? $"CN-{rng.Next(9000) + 1000}" : $"{rng.Pick(["INV-", "", "A-"])}{invoiceDate.Year}-{rng.Next(99999):00000}",
            InvoiceDate: invoiceDate,
            DueDate: dueDate,
            DateFormat: rng.Pick(DateFormats),
            Money: money,
            Lines: lines,
            DiscountLabel: discountLabel,
            Discount: discount,
            TaxLines: taxLines,
            Notes: notes);
    }

    private static Party Party(HashRng rng, string name)
    {
        var a = rng.Pick(Addresses);
        return new Party(name, new Address(a.Street, a.City, a.Region, a.Postal, a.Country));
    }

    private static decimal Round(decimal value, int decimals) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Deterministic random stream: SHA-256 of "seed:key:counter".</summary>
    private sealed class HashRng(int seed, string key)
    {
        private int counter;

        public int Next(int maxExclusive)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{key}:{counter++}"));
            return (int)(BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)maxExclusive);
        }

        public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];
    }
}
