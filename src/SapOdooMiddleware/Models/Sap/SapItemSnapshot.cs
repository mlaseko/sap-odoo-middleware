namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Read-only snapshot of the fields Item Provisioning cares about on an existing
/// SAP OITM item: the Odoo category UDF and the three price-list prices. Used to
/// drive idempotent recovery (fill only blank fields, never overwrite real values).
/// </summary>
public record SapItemSnapshot(
    string? OdooCategoryUdf,
    decimal RetailPrice,
    decimal DealerPrice,
    decimal SuperDealerPrice);
