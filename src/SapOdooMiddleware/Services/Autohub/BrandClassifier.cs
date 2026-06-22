namespace SapOdooMiddleware.Services.Autohub;

/// <summary>
/// Decides whether an invoice line's brand identifies the SAME supplier as a candidate donor SAP item.
/// A SAP item represents exactly one (supplier, article) pair, so we may only auto-match a line to an
/// existing item when the suppliers agree. Vehicle-group brands (VAG, BMW, …) are Molas's internal SKU
/// prefixes — NOT supplier names — so a donor under one is ambiguous and must be confirmed by an operator.
/// </summary>
public static class BrandClassifier
{
    /// <summary>Internal SKU/vehicle-group prefixes that are NOT supplier company names.</summary>
    public static readonly IReadOnlySet<string> VehicleGroupBrands =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VAG", "BMW", "MB", "LR", "OE", "FORD", "VOLVO", "MINI"
        };

    public enum MatchKind
    {
        SameSupplier,
        VehicleGroupBrand,
        DifferentSupplier,
        NoBrandOnInvoice
    }

    public static MatchKind Classify(string? invoiceBrand, string? donorSupplier)
    {
        var invoice = (invoiceBrand ?? string.Empty).Trim();
        var donor = (donorSupplier ?? string.Empty).Trim();

        if (invoice.Length == 0)
            return MatchKind.NoBrandOnInvoice;

        if (string.Equals(invoice, donor, StringComparison.OrdinalIgnoreCase))
            return MatchKind.SameSupplier;

        if (VehicleGroupBrands.Contains(invoice))
            return MatchKind.VehicleGroupBrand;

        return MatchKind.DifferentSupplier;
    }
}
