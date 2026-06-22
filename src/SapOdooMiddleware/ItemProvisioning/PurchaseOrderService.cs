using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.ItemProvisioning;

public record PoPreviewLine(Guid LineId, string ItemCode, string? Description, double Quantity, double UnitPrice, double DiscountPct);

public record PoPreview(
    bool Ready,
    string? CardCode,
    string? CardName,
    string? Currency,
    string? NumAtCard,
    string? Comments,
    string Warehouse,
    List<PoPreviewLine> Lines,
    List<string> Blocking);

public record PoPostLine(string ItemCode, double Quantity, double UnitPrice, double DiscountPct);

public record PoPostResult(bool Ok, int? DocEntry, int? DocNum, string? Error, bool AlreadyExists = false);

/// <summary>
/// Builds a Purchase Order preview from a reviewed invoice and posts the (user-edited) PO to SAP B1.
/// Vendor, currency, NumAtCard (invoice no.) and Comments (SO no.) are derived from the invoice — only the
/// lines are user-editable. Skip/promotional lines are excluded and never block. No VAT is set (inherited
/// from the vendor BP's tax group).
/// </summary>
public class PurchaseOrderService
{
    private readonly IStagingDocumentRepository _docs;
    private readonly IStagingDocumentLineRepository _lines;
    private readonly ISapB1Service _sap;
    private readonly PurchaseOrderSettings _settings;
    private readonly ILogger<PurchaseOrderService> _logger;

    public PurchaseOrderService(
        IStagingDocumentRepository docs,
        IStagingDocumentLineRepository lines,
        ISapB1Service sap,
        IOptions<PurchaseOrderSettings> settings,
        ILogger<PurchaseOrderService> logger)
    {
        _docs = docs;
        _lines = lines;
        _sap = sap;
        _settings = settings.Value;
        _logger = logger;
    }

    private VendorMapping? ResolveVendor(string? supplier) =>
        string.IsNullOrWhiteSpace(supplier)
            ? null
            : _settings.Vendors.FirstOrDefault(v =>
                !string.IsNullOrWhiteSpace(v.Match)
                && supplier.Contains(v.Match, StringComparison.OrdinalIgnoreCase));

    public async Task<PoPreview> BuildPreviewAsync(StagingDocumentRow doc, CancellationToken ct)
    {
        var vendor = ResolveVendor(doc.Supplier);
        var allLines = await _lines.ListByDocumentAsync(doc.Id, ct);

        // Sellable = posted to SAP (matched/created). Skip/promotional are excluded and never block.
        var sellable = allLines
            .Where(l => l.ReviewStatus is "matched" or "created")
            .Select(l => new PoPreviewLine(
                l.Id,
                (l.MatchedSku ?? l.CreatedSku ?? "").Trim(),
                l.Description,
                (double)(l.Quantity ?? 0m),
                (double)(l.UnitPrice ?? 0m),
                (double)l.DiscountPct))   // carry the invoice discount (100% on free-bonus lines → £0 on the PO)
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemCode))
            .ToList();

        var blocking = new List<string>();
        if (vendor is null)
            blocking.Add($"Supplier '{doc.Supplier ?? "(none)"}' is not mapped to a SAP vendor — pick/confirm the vendor.");
        if (string.IsNullOrWhiteSpace(doc.Currency))
            blocking.Add("Invoice currency is missing.");

        // Any non-skip, non-promotional line that isn't yet matched/created can't go on the PO.
        var notReady = allLines
            .Where(l => !l.IsPromotional
                        && l.ReviewStatus is not ("matched" or "created" or "skip"))
            .Select(l => l.ArticleNumber ?? l.Id.ToString())
            .ToList();
        if (notReady.Count > 0)
            blocking.Add($"{notReady.Count} sellable line(s) not yet in SAP (matched/created): {string.Join(", ", notReady.Take(15))}.");

        if (sellable.Count == 0)
            blocking.Add("No sellable (matched/created) lines to order.");

        return new PoPreview(
            Ready: blocking.Count == 0,
            CardCode: vendor?.CardCode,
            CardName: vendor?.CardName,
            Currency: doc.Currency,
            NumAtCard: doc.InvoiceNumber,
            Comments: BuildComments(doc),
            Warehouse: _settings.DefaultWarehouse,
            Lines: sellable,
            Blocking: blocking);
    }

    public async Task<PoPostResult> PostAsync(StagingDocumentRow doc, List<PoPostLine> lines, CancellationToken ct)
    {
        var vendor = ResolveVendor(doc.Supplier);
        if (vendor is null)
            return new PoPostResult(false, null, null,
                $"Supplier '{doc.Supplier ?? "(none)"}' is not mapped to a SAP vendor.");

        if (string.IsNullOrWhiteSpace(doc.Currency))
            return new PoPostResult(false, null, null, "Invoice currency is missing.");

        var clean = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemCode) && l.Quantity > 0)
            .ToList();
        if (clean.Count == 0)
            return new PoPostResult(false, null, null, "No valid PO lines (each needs an ItemCode and quantity > 0).");

        // Every line's item must exist in OITM.
        var missing = new List<string>();
        foreach (var l in clean)
        {
            if (!await _sap.ItemExistsAsync(l.ItemCode.Trim()))
                missing.Add(l.ItemCode.Trim());
        }
        if (missing.Count > 0)
            return new PoPostResult(false, null, null,
                $"These items are not in SAP (OITM): {string.Join(", ", missing.Distinct())}.");

        // Dedup by vendor reference (invoice number).
        var numAtCard = doc.InvoiceNumber;
        if (!string.IsNullOrWhiteSpace(numAtCard))
        {
            var existing = await _sap.FindPurchaseOrderByNumAtCardAsync(vendor.CardCode, numAtCard);
            if (existing is { } e)
                return new PoPostResult(false, e.DocEntry, e.DocNum,
                    $"A Purchase Order already exists for invoice '{numAtCard}' (DocNum {e.DocNum}).",
                    AlreadyExists: true);
        }

        var req = new SapPurchaseOrderRequest
        {
            CardCode = vendor.CardCode,
            Currency = doc.Currency,
            NumAtCard = numAtCard,
            Comments = BuildComments(doc),
            DocDate = DateTime.Today,
            Lines = clean.Select(l => new SapPurchaseOrderLineRequest
            {
                ItemCode = l.ItemCode.Trim(),
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPercent = l.DiscountPct,
                WarehouseCode = _settings.DefaultWarehouse,
            }).ToList(),
        };

        try
        {
            var resp = await _sap.CreatePurchaseOrderAsync(req);
            _logger.LogInformation(
                "PO created for document {DocId}: DocNum={DocNum}, vendor={CardCode}, lines={Lines}.",
                doc.Id, resp.DocNum, vendor.CardCode, req.Lines.Count);
            return new PoPostResult(true, resp.DocEntry, resp.DocNum, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PO creation failed for document {DocId} (vendor {CardCode}).", doc.Id, vendor.CardCode);
            return new PoPostResult(false, null, null, ex.Message);
        }
    }

    private static string? BuildComments(StagingDocumentRow doc) =>
        string.IsNullOrWhiteSpace(doc.SalesOrder) ? null : $"SO: {doc.SalesOrder}";
}
