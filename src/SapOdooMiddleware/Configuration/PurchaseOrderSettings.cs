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
    /// Phase B (5 Nov 2026): before the PO is posted, write the final invoice into its Odoo container order
    /// (<c>supplier.order.dgx_apply_final_invoice</c>). A refusal stops the PO.
    /// </summary>
    public bool WriteFinalInvoiceToOdoo { get; set; } = true;

    /// <summary>Invoice PDFs larger than this are not attached to the Odoo order.</summary>
    public long MaxPdfBytesToOdoo { get; set; } = 15 * 1024 * 1024;

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
