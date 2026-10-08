namespace InvoiceEvals.Core;

/// <summary>
/// Extraction target. Fields are derived from the FATURA annotation classes.
/// A null value means the field is not printed on the document.
/// See docs/annotation-guidelines.md for normalization rules.
/// </summary>
public sealed record InvoiceDto(
    Party? Vendor,
    Party? Customer,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? Currency,
    decimal? Subtotal,
    decimal? Discount,
    decimal? Tax,
    decimal? Total,
    IReadOnlyList<LineItem>? LineItems);

public sealed record Party(string? Name, Address? Address);

/// <param name="Region">State or province code, e.g. "AK".</param>
/// <param name="Country">ISO 3166-1 alpha-2 code.</param>
public sealed record Address(string? Street, string? City, string? Region, string? PostalCode, string? Country);

public sealed record LineItem(string Description, decimal? Quantity, decimal? UnitPrice, decimal? Amount);
