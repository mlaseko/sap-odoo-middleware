namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating an AR Invoice in SAP B1.
/// </summary>
public class SapInvoiceResponse
{
    /// <summary>SAP Invoice DocEntry (internal key). Maps to OINV.DocEntry.</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Invoice DocNum (user-facing number). Maps to OINV.DocNum.</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// The Odoo invoice reference written onto the SAP Invoice header
    /// as <c>NumAtCard</c> and UDF <c>U_Odoo_Invoice_ID</c>.
    /// </summary>
    public string ExternalInvoiceId { get; set; } = string.Empty;

    /// <summary>
    /// SAP Delivery Note DocEntry that this invoice was copied from (if applicable).
    /// </summary>
    public int? BaseDeliveryDocEntry { get; set; }

    /// <summary>
    /// SAP Sales Order DocEntry referenced by the originating delivery/invoice.
    /// </summary>
    public int? BaseSalesOrderDocEntry { get; set; }
}
