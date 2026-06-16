namespace SapOdooMiddleware.Configuration;

/// <summary>
/// Settings for creating Purchase Orders in SAP B1 from reviewed invoices.
/// Bound from the "PurchaseOrders" configuration section.
/// </summary>
public class PurchaseOrderSettings
{
    public const string SectionName = "PurchaseOrders";

    /// <summary>Default receiving warehouse code for PO lines (SAP OWHS.WhsCode).</summary>
    public string DefaultWarehouse { get; set; } = "MainWHSE";

    /// <summary>
    /// Invoice-supplier → SAP vendor BP mappings. The invoice's <c>Supplier</c> is matched
    /// case-insensitively against <see cref="VendorMapping.Match"/> (substring). For Lubes there is a
    /// single vendor (Liqui Moly → S00001); Meguin products invoice under the same vendor.
    /// </summary>
    public List<VendorMapping> Vendors { get; set; } = new()
    {
        new VendorMapping { Match = "liqui moly", CardCode = "S00001", CardName = "LIQUI MOLY GMB" },
    };
}

public class VendorMapping
{
    /// <summary>Case-insensitive substring matched against the invoice supplier name.</summary>
    public string Match { get; set; } = string.Empty;
    public string CardCode { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
}
