using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request to create a Purchase Order in SAP B1 (<c>oPurchaseOrders</c>) from a reviewed invoice.
/// VAT is intentionally not set per line — the vendor BP carries its tax group in SAP, which the PO
/// inherits (the configured vendors are exempt, so no VAT is charged).
/// </summary>
public class SapPurchaseOrderRequest
{
    /// <summary>SAP vendor Business Partner card code (e.g. S00001).</summary>
    [Required]
    public string CardCode { get; set; } = string.Empty;

    /// <summary>Document currency (the invoice currency).</summary>
    public string? Currency { get; set; }

    /// <summary>Vendor reference — written to <c>OPOR.NumAtCard</c> (the invoice number).</summary>
    public string? NumAtCard { get; set; }

    /// <summary>Free-text remarks — written to <c>OPOR.Comments</c> (the Sales Order number).</summary>
    public string? Comments { get; set; }

    /// <summary>Posting date; defaults to today when null.</summary>
    public DateTime? DocDate { get; set; }

    [Required]
    [MinLength(1)]
    public List<SapPurchaseOrderLineRequest> Lines { get; set; } = [];
}

public class SapPurchaseOrderLineRequest
{
    [Required]
    public string ItemCode { get; set; } = string.Empty;

    [Range(0.0001, double.MaxValue)]
    public double Quantity { get; set; }

    public double UnitPrice { get; set; }

    /// <summary>Receiving warehouse; defaults to the configured default when null.</summary>
    public string? WarehouseCode { get; set; }
}

public class SapPurchaseOrderResponse
{
    public int DocEntry { get; set; }
    public int DocNum { get; set; }
    public string? NumAtCard { get; set; }
}
