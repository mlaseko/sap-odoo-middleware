using System.ComponentModel.DataAnnotations;

namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request payload sent from Odoo to create or update a Sales Employee
/// in SAP B1 OSLP table via DI API.
/// </summary>
public class SapSalesEmployeeRequest
{
    /// <summary>
    /// Sales employee name. Maps to SAP B1 <c>SlpName</c> on the OSLP table.
    /// </summary>
    [Required]
    public string SlpName { get; set; } = string.Empty;

    /// <summary>
    /// Odoo hr.employee ID — stored in SAP B1 UDF <c>U_OdooEmployeeId</c>
    /// on the OSLP table. Used for bi-directional linking.
    /// </summary>
    [Required]
    public string OdooEmployeeId { get; set; } = string.Empty;
}
