namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating or updating a Customer
/// (BusinessPartner) in SAP B1.
/// </summary>
public class SapCustomerResponse
{
    /// <summary>SAP Business Partner CardCode (e.g. "C00042").</summary>
    public string CardCode { get; set; } = string.Empty;

    /// <summary>Customer name as stored in SAP.</summary>
    public string CardName { get; set; } = string.Empty;

    /// <summary>
    /// Odoo res.partner ID passed through from the request.
    /// Returned so the Odoo ICC <c>_writeback_partner()</c> can locate the record.
    /// </summary>
    public string OdooCustomerId { get; set; } = string.Empty;

    /// <summary>Whether this was a create or update operation.</summary>
    public string Operation { get; set; } = "created";
}
