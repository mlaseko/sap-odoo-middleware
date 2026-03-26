using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create or update a Customer (BusinessPartner)
/// in SAP B1 via DI API.
/// </summary>
public class SapCustomerRequest
{
    /// <summary>
    /// Customer name.  Maps to SAP B1 <c>CardName</c> on the OCRD table.
    /// </summary>
    [Required]
    public string CardName { get; set; } = string.Empty;

    /// <summary>Primary phone number. Maps to SAP B1 <c>Phone1</c>.</summary>
    [Required]
    public string Phone1 { get; set; } = string.Empty;

    /// <summary>Secondary phone number. Maps to SAP B1 <c>Phone2</c>.</summary>
    public string? Phone2 { get; set; }

    /// <summary>Email address. Maps to SAP B1 <c>EmailAddress</c>.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// Odoo res.partner ID — stored in SAP B1 header UDF <c>U_OdooCustomerId</c>.
    /// Used by <c>_writeback_partner()</c> to link the SAP CardCode back to the
    /// correct Odoo record.
    /// </summary>
    [Required]
    public string OdooCustomerId { get; set; } = string.Empty;

    /// <summary>
    /// SAP customer group code (default 100 = General).
    /// Maps to SAP B1 <c>GroupCode</c>.
    /// </summary>
    public int GroupCode { get; set; } = 100;

    /// <summary>
    /// SAP Sales Employee Code from OSLP.SlpCode.
    /// Maps to SAP B1 <c>OCRD.SlpCode</c> (the sales employee assigned to this customer).
    /// When null or -1, no sales employee is assigned.
    /// </summary>
    public int? SlpCode { get; set; }

    /// <summary>
    /// SAP Price List Number from OPLN.ListNum.
    /// Maps to SAP B1 <c>OCRD.ListNum</c> (the price list assigned to this customer).
    /// When null, the SAP default price list is used.
    /// </summary>
    public int? PriceListNum { get; set; }

    /// <summary>Bill-to address.</summary>
    public SapCustomerAddressRequest? BillTo { get; set; }

    /// <summary>Ship-to address.</summary>
    public SapCustomerAddressRequest? ShipTo { get; set; }
}

public class SapCustomerAddressRequest
{
    public string Street { get; set; } = string.Empty;
    public string? City { get; set; }
    public string Country { get; set; } = "TZ";
    public string? ZipCode { get; set; }
    public string? State { get; set; }
}
