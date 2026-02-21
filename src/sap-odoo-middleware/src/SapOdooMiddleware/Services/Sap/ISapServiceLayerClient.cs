using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Services.Sap;

public interface ISapServiceLayerClient
{
    Task<SalesOrderResponse> CreateSalesOrderAsync(SalesOrderRequest request);
    Task LoginAsync();
    Task<bool> IsHealthyAsync();
}
