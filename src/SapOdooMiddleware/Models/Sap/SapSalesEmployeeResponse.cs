namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating or updating a Sales Employee
/// in SAP B1.
/// </summary>
public class SapSalesEmployeeResponse
{
    /// <summary>SAP Sales Employee Code from OSLP.SlpCode.</summary>
    public int SlpCode { get; set; }

    /// <summary>Sales employee name as stored in SAP.</summary>
    public string SlpName { get; set; } = string.Empty;

    /// <summary>Odoo hr.employee ID passed through from the request.</summary>
    public string OdooEmployeeId { get; set; } = string.Empty;

    /// <summary>Whether this was a create or update operation.</summary>
    public string Operation { get; set; } = "created";
}
