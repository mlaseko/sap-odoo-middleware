using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Services.Odoo;

public interface IOdooJsonRpcClient
{
    Task<DeliveryConfirmationResponse> ConfirmDeliveryAsync(DeliveryConfirmationRequest request);
    Task<bool> IsHealthyAsync();
}
