using SapOdooMiddleware.Models.Inventory;

namespace SapOdooMiddleware.Services;

/// <summary>
/// Marker for the SAP B1 DI-API connection bound to the <b>Autohub</b> company
/// (<c>Companies:Autohub:SapB1</c>) — a second, independent persistent connection distinct from the
/// default <see cref="ISapB1Service"/> (which serves the Lubes / top-level company).
///
/// Autohub spare-parts item creation resolves <em>this</em> service so items land in the Autohub
/// company (e.g. "Molas Live 2021"), not the Lubes company. It uses its own license seat and its own
/// lock, so Lubes and Autohub SAP work do not contend.
/// </summary>
public interface IAutohubSapB1Service : ISapB1Service
{
    /// <summary>
    /// Creates one A/P invoice <b>draft</b> (ODRF, <c>DocObjectCode = oPurchaseInvoices</c>) whose lines
    /// copy the given open GRPO lines (BaseType 20 + BaseEntry/BaseLine, quantity = open quantity), so
    /// price, tax and warehouse come from the GRPOs. <paramref name="currency"/> is the GRPOs' shared
    /// currency and rate, set on the draft when foreign; <paramref name="bplId"/> their shared branch
    /// (mandatory in a company with branches). DocDueDate is left to SAP (supplier payment
    /// terms). Never adds the invoice. When <see cref="PurchaseInvoiceDraftCreate.ExpectedTotal"/> is
    /// set and SAP's total differs by more than 0.01, the draft is removed and
    /// <see cref="PurchaseInvoiceDraftTotalMismatchException"/> is thrown.
    /// </summary>
    Task<PurchaseInvoiceDraftResult> CreatePurchaseInvoiceDraftAsync(
        PurchaseInvoiceDraftCreate request,
        IReadOnlyList<(int GrpoDocEntry, GrpoOpenLine Line)> lines,
        PurchaseDocCurrency currency,
        int? bplId,
        CancellationToken ct);
}
