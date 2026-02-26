using SapOdooMiddleware.Models.Sap;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Non-Windows stub for <see cref="ISapB1Service"/>.
/// The SAP B1 DI API requires the Windows COM runtime; this implementation throws
/// <see cref="PlatformNotSupportedException"/> at runtime on non-Windows hosts.
/// It is used only for builds where the SAPbobsCOM COM reference is unavailable
/// (e.g. Linux CI, development on macOS).
/// </summary>
internal sealed class SapB1DiApiServiceStub : ISapB1Service
{
    public Task<SapB1PingResponse> PingAsync() =>
        throw new PlatformNotSupportedException(
            "SAP B1 DI API is only supported on Windows. Deploy to a Windows host.");

    public Task<SapSalesOrderResponse> CreateSalesOrderAsync(SapSalesOrderRequest request) =>
        throw new PlatformNotSupportedException(
            "SAP B1 DI API is only supported on Windows. Deploy to a Windows host.");

    public Task<SapSalesOrderResponse> UpdateSalesOrderAsync(int docEntry, SapSalesOrderRequest request) =>
        throw new PlatformNotSupportedException(
            "SAP B1 DI API is only supported on Windows. Deploy to a Windows host.");

    public Task<SapInvoiceResponse> CreateInvoiceAsync(SapInvoiceRequest request) =>
        throw new PlatformNotSupportedException(
            "SAP B1 DI API is only supported on Windows. Deploy to a Windows host.");

    public Task<SapIncomingPaymentResponse> CreateIncomingPaymentAsync(SapIncomingPaymentRequest request) =>
        throw new PlatformNotSupportedException(
            "SAP B1 DI API is only supported on Windows. Deploy to a Windows host.");
}
