using SapOdooMiddleware.Models.Sap;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Abstraction over the SAP B1 DI API for sales-order and pick-list operations.
/// </summary>
public interface ISapB1Service
{
    /// <summary>
    /// Creates a Sales Order in SAP B1 via DI API and optionally a Pick List.
    /// </summary>
    Task<SapSalesOrderResponse> CreateSalesOrderAsync(SapSalesOrderRequest request);
}
