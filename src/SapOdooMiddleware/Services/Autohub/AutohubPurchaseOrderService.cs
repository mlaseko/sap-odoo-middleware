using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Inventory;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>A line that goes on the purchase order. <c>PriceSource</c>: "created" (the PL01 Bulk Create stored)
/// or "calculated" (PL01 worked out from this invoice's price, for a part that already existed in SAP).</summary>
public sealed record AutohubPoLine(
    Guid LineId, int LineNumber, string ItemCode, string? SupplierArticleNumber, string? Description,
    decimal Quantity, decimal UnitPrice, decimal LineTotal, string PriceSource);

/// <summary>An invoice line left off the purchase order, and why.</summary>
public sealed record AutohubPoExcludedLine(int LineNumber, string? Description, string Reason);

/// <summary>The vendor the purchase order would go to.</summary>
public sealed record AutohubPoVendor(string CardCode, string? CardName, string? Currency, bool Remembered);

/// <summary>What "Create purchase order" would post now, and what stops it.</summary>
public sealed record AutohubPoPreview(
    Guid DocumentId, string DocumentStatus, string? SupplierName, string? InvoiceNumber, DateTime? InvoiceDate,
    string? InvoiceCurrency, decimal? InvoiceTotal,
    AutohubPoVendor? Vendor, string Warehouse, string Currency,
    IReadOnlyList<AutohubPoLine> Lines, IReadOnlyList<AutohubPoExcludedLine> Excluded,
    decimal DocTotal, bool Ready, IReadOnlyList<string> Blocking,
    AutohubPurchaseOrderRecord? PurchaseOrder);

/// <summary>Outcome of "Create purchase order". <c>Status</c> is the HTTP status to answer with.</summary>
public sealed record AutohubPoCreateResult(
    int Status, AutohubPurchaseOrderRecord? PurchaseOrder, bool AlreadyExisted, IReadOnlyList<string> Errors);

public interface IAutohubPurchaseOrderService
{
    /// <summary>The preview for the invoice, with <paramref name="cardCode"/> or else the remembered vendor; null when no invoice.</summary>
    Task<AutohubPoPreview?> PreviewAsync(Guid documentId, string? cardCode, CancellationToken ct);

    /// <summary>Creates the SAP purchase order (Autohub company) from the reviewed invoice. Idempotent per invoice.</summary>
    Task<AutohubPoCreateResult> CreateAsync(Guid documentId, string cardCode, string user, CancellationToken ct);
}

/// <summary>
/// Autohub purchase order from a reviewed invoice: one PO in TZS at PL01 cost, every line to the configured
/// warehouse (003 Mikocheni), vendor picked by the operator and remembered per supplier name. Matched and
/// created lines go on it; skipped, promotional and free (100% discount) lines are left off. Lines created by
/// Bulk Create use the PL01 it stored; matched lines get PL01 from this invoice's price exactly as Bulk Create
/// works it out (price less discount × forex rate → pricing engine cost). Posted through the Autohub SAP
/// connection with the branch of the warehouse, NumAtCard = invoice number, U_AppRef = invoice id.
/// </summary>
public sealed class AutohubPurchaseOrderService : IAutohubPurchaseOrderService
{
    internal const string PoCurrency = "TZS";

    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IAutohubPurchaseOrderRepository _repo;
    private readonly IAutohubInventorySqlService _sql;
    private readonly IAutohubSapB1Service _sap;
    private readonly IForexConversionService _forex;
    private readonly IPricingCalculationService _pricing;
    private readonly IInvoiceActivityLog _activity;
    private readonly AutohubInventorySettings _settings;
    private readonly ILogger<AutohubPurchaseOrderService> _logger;

    public AutohubPurchaseOrderService(
        IStagingPartsDocumentRepository docs,
        IAutohubPurchaseOrderRepository repo,
        IAutohubInventorySqlService sql,
        IAutohubSapB1Service sap,
        IForexConversionService forex,
        IPricingCalculationService pricing,
        IInvoiceActivityLog activity,
        IOptions<AutohubInventorySettings> settings,
        ILogger<AutohubPurchaseOrderService> logger)
    {
        _docs = docs;
        _repo = repo;
        _sql = sql;
        _sap = sap;
        _forex = forex;
        _pricing = pricing;
        _activity = activity;
        _settings = settings.Value;
        _logger = logger;
    }

    private string Warehouse => string.IsNullOrWhiteSpace(_settings.PurchaseOrderWarehouse)
        ? "003" : _settings.PurchaseOrderWarehouse.Trim();

    public async Task<AutohubPoPreview?> PreviewAsync(Guid documentId, string? cardCode, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        return doc is null ? null : await BuildPreviewAsync(doc, cardCode, ct);
    }

    private async Task<AutohubPoPreview> BuildPreviewAsync(StagingPartsDocumentRow doc, string? cardCode, CancellationToken ct)
    {
        var blocking = new List<string>();
        var existingPo = await _repo.GetPurchaseOrderAsync(doc.Id, ct);

        if (doc.Status != "reviewed")
            blocking.Add($"The invoice is '{doc.Status}': complete the review first (every line created, matched or skipped).");

        // Vendor: the one asked for, else the one remembered for this supplier name.
        AutohubPoVendor? vendor = null;
        bool remembered = false;
        if (string.IsNullOrWhiteSpace(cardCode) && !string.IsNullOrWhiteSpace(doc.SupplierName))
        {
            var saved = await _repo.GetVendorAsync(doc.SupplierName, ct);
            if (saved is not null) { cardCode = saved.CardCode; remembered = true; }
        }
        if (string.IsNullOrWhiteSpace(cardCode))
            blocking.Add("Choose the SAP vendor.");
        else
        {
            var bp = await _sql.GetVendorAsync(cardCode.Trim(), ct);
            if (bp is null)
                blocking.Add($"Vendor {cardCode.Trim()} is not a supplier in SAP.");
            else
            {
                vendor = new AutohubPoVendor(bp.CardCode, bp.CardName, bp.Currency, remembered);
                if (bp.Frozen)
                    blocking.Add($"Vendor {bp.CardCode} is inactive in SAP.");
                if (!string.IsNullOrWhiteSpace(bp.Currency) && bp.Currency != "##"
                    && !string.Equals(bp.Currency, PoCurrency, StringComparison.OrdinalIgnoreCase))
                    blocking.Add($"Vendor {bp.CardCode} is set to currency {bp.Currency} in SAP; the purchase order is in {PoCurrency}.");
            }
        }

        var source = await _repo.ListLinesAsync(doc.Id, ct);
        var (include, excluded, lineProblems) = Classify(source);
        blocking.AddRange(lineProblems);

        // Prices: the stored PL01 of created lines; this invoice's PL01 for the rest.
        var lines = new List<AutohubPoLine>();
        foreach (var l in include)
        {
            var (price, priceSource, error) = await Pl01Async(l, doc.Currency, ct);
            if (error is not null) { blocking.Add($"Line {l.LineNumber} ({l.ItemCode}): {error}"); continue; }
            var qty = l.Quantity!.Value;
            lines.Add(new AutohubPoLine(l.Id, l.LineNumber, l.ItemCode!, l.SupplierArticleNumber, l.Description,
                qty, price, Math.Round(qty * price, 2, MidpointRounding.AwayFromZero), priceSource));
        }

        // Every item must be a purchasable, active SAP item.
        if (lines.Count > 0)
        {
            var status = (await _sql.GetItemPurchaseStatusAsync(lines.Select(l => l.ItemCode).ToList(), ct))
                .ToDictionary(s => s.ItemCode, StringComparer.OrdinalIgnoreCase);
            var missing = lines.Where(l => !status.ContainsKey(l.ItemCode)).Select(l => l.ItemCode).Distinct().ToList();
            var notPurchase = lines.Where(l => status.TryGetValue(l.ItemCode, out var s) && !s.PurchaseItem)
                .Select(l => l.ItemCode).Distinct().ToList();
            var frozen = lines.Where(l => status.TryGetValue(l.ItemCode, out var s) && s.Frozen)
                .Select(l => l.ItemCode).Distinct().ToList();
            if (missing.Count > 0) blocking.Add($"Not in SAP: {Join(missing)}.");
            if (notPurchase.Count > 0) blocking.Add($"Not purchase items in SAP: {Join(notPurchase)}.");
            if (frozen.Count > 0) blocking.Add($"Inactive in SAP: {Join(frozen)}.");
        }
        else if (lineProblems.Count == 0)
            blocking.Add("No line to order: every line is skipped, promotional or free.");

        return new AutohubPoPreview(
            doc.Id, doc.Status, doc.SupplierName, doc.InvoiceNumber, doc.InvoiceDate, doc.Currency, doc.TotalAmount,
            vendor, Warehouse, PoCurrency, lines, excluded, lines.Sum(l => l.LineTotal),
            Ready: blocking.Count == 0 && existingPo is null, blocking, existingPo);
    }

    /// <summary>
    /// Which lines go on the purchase order. Matched and created lines with an item and a quantity are
    /// included; skipped, promotional and free (100% discount) lines are left off; any other line (not yet
    /// in SAP) or a matched/created line without an item or quantity is a problem that blocks the PO.
    /// </summary>
    internal static (List<AutohubPoSourceLine> Include, List<AutohubPoExcludedLine> Excluded, List<string> Problems)
        Classify(IReadOnlyList<AutohubPoSourceLine> lines)
    {
        var include = new List<AutohubPoSourceLine>();
        var excluded = new List<AutohubPoExcludedLine>();
        var problems = new List<string>();
        foreach (var l in lines)
        {
            if (l.IsPromotional) { excluded.Add(new(l.LineNumber, l.Description, "promotional")); continue; }
            if (l.ReviewStatus == "skip") { excluded.Add(new(l.LineNumber, l.Description, "skipped")); continue; }
            if (l.ReviewStatus is not ("matched" or "created"))
            {
                problems.Add($"Line {l.LineNumber} is '{l.ReviewStatus}', not in SAP yet.");
                continue;
            }
            if (l.DiscountPct is >= 100m) { excluded.Add(new(l.LineNumber, l.Description, "free (100% discount)")); continue; }
            if (string.IsNullOrWhiteSpace(l.ItemCode)) { problems.Add($"Line {l.LineNumber} has no SAP item code."); continue; }
            if (l.Quantity is not > 0m) { problems.Add($"Line {l.LineNumber} ({l.ItemCode}) has no quantity."); continue; }
            include.Add(l);
        }
        return (include, excluded, problems);
    }

    /// <summary>
    /// PL01 for one line. Created: the PL01 Bulk Create stored. Otherwise: as Bulk Create works it out —
    /// the invoice unit price less its discount, converted to TZS at the forex rate, through the pricing
    /// engine (cost markup) — so a part that already existed is costed at this invoice's price.
    /// </summary>
    private async Task<(decimal Price, string Source, string? Error)> Pl01Async(
        AutohubPoSourceLine line, string? currency, CancellationToken ct)
    {
        if (line.ReviewStatus == "created" && line.Pl01Tzs is > 0m)
            return (Math.Round(line.Pl01Tzs.Value, 2, MidpointRounding.AwayFromZero), "created", null);

        if (line.UnitPriceForeign is not > 0m)
            return (0m, "calculated", "no unit price on the invoice line.");
        if (string.IsNullOrWhiteSpace(currency))
            return (0m, "calculated", "the invoice has no currency.");

        var unit = EffectiveUnitPrice(line.UnitPriceForeign.Value, line.DiscountPct);
        try
        {
            var costTzs = string.Equals(currency.Trim(), PoCurrency, StringComparison.OrdinalIgnoreCase)
                ? unit
                : await _forex.ConvertToTzsAsync(unit, currency.Trim(), DateTime.UtcNow, ct);
            var prices = await _pricing.CalculateAsync(costTzs, line.Brand ?? "", ct);
            if (prices.Cost <= 0m)
                return (0m, "calculated", "PL01 works out at zero.");
            return (Math.Round(prices.Cost, 2, MidpointRounding.AwayFromZero), "calculated", null);
        }
        catch (Exception ex)
        {
            return (0m, "calculated", $"PL01 could not be worked out ({ex.Message}).");
        }
    }

    /// <summary>The invoice unit price less the line discount (a discount of 0 or ≥ 100 is not applied), as Bulk Create does.</summary>
    internal static decimal EffectiveUnitPrice(decimal unitPrice, decimal? discountPct) =>
        discountPct is > 0m and < 100m
            ? Math.Round(unitPrice * (1m - discountPct.Value / 100m), 6, MidpointRounding.AwayFromZero)
            : unitPrice;

    public async Task<AutohubPoCreateResult> CreateAsync(Guid documentId, string cardCode, string user, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(documentId, ct);
        if (doc is null)
            return new(404, null, false, new[] { "Invoice not found." });

        // One purchase order per invoice: the one already recorded, or found in SAP by U_AppRef.
        var recorded = await _repo.GetPurchaseOrderAsync(documentId, ct);
        if (recorded is not null)
            return new(200, recorded, true, Array.Empty<string>());

        string appRef = documentId.ToString();
        var inSap = await _sql.FindDocEntryByAppRefAsync("OPOR", appRef, ct);
        if (inSap is { } found)
        {
            var record = new AutohubPurchaseOrderRecord(documentId, cardCode.Trim(), found.DocEntry, found.DocNum, 0, 0m, user, DateTime.UtcNow);
            await TrySaveRecordAsync(record, ct);
            return new(200, record, true, Array.Empty<string>());
        }

        if (string.IsNullOrWhiteSpace(cardCode))
            return new(400, null, false, new[] { "card_code (the SAP vendor) is required." });

        var preview = await BuildPreviewAsync(doc, cardCode.Trim(), ct);
        if (preview.Blocking.Count > 0)
            return new(400, null, false, preview.Blocking);
        var vendor = preview.Vendor!;

        // One purchase order per supplier invoice number.
        var invoiceNumber = string.IsNullOrWhiteSpace(doc.InvoiceNumber) ? null : Clip(doc.InvoiceNumber.Trim(), 100);
        if (invoiceNumber is not null)
        {
            var dup = await _sap.FindPurchaseOrderByNumAtCardAsync(vendor.CardCode, invoiceNumber);
            if (dup is { } d)
                return new(409, null, false, new[]
                {
                    $"Vendor {vendor.CardCode} already has purchase order {d.DocNum} for invoice {invoiceNumber}. Nothing was created.",
                });
        }

        // Branch (OPOR.BPLId) of the receiving warehouse: mandatory in the Autohub company.
        var branch = await _sql.GetWarehouseBranchAsync(preview.Warehouse, ct);
        if (branch is null || !branch.Active)
            return new(422, null, false, new[]
            {
                branch is null
                    ? $"Warehouse {preview.Warehouse} is not assigned to a SAP branch."
                    : $"Warehouse {preview.Warehouse} is in branch {branch.BplId} ('{branch.BranchName}'), which is not active in SAP.",
            });

        var request = new SapPurchaseOrderRequest
        {
            CardCode = vendor.CardCode,
            Currency = PoCurrency,
            NumAtCard = invoiceNumber,
            Comments = BuildComments(doc),
            DocDate = DateTime.Today,
            TaxDate = doc.InvoiceDate?.Date,
            BplId = branch.BplId,
            AppRef = appRef,
            Lines = preview.Lines.Select(l => new SapPurchaseOrderLineRequest
            {
                ItemCode = l.ItemCode,
                Quantity = (double)l.Quantity,
                UnitPrice = (double)l.UnitPrice,
                DiscountPercent = 0,
                WarehouseCode = preview.Warehouse,
            }).ToList(),
        };

        SapPurchaseOrderResponse resp;
        try
        {
            resp = await _sap.CreatePurchaseOrderAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autohub PO creation failed for invoice {DocId} (vendor {CardCode}).", documentId, vendor.CardCode);
            return new(500, null, false, new[] { ex.Message });
        }

        var po = new AutohubPurchaseOrderRecord(documentId, vendor.CardCode, resp.DocEntry, resp.DocNum,
            preview.Lines.Count, preview.DocTotal, user, DateTime.UtcNow);
        await TrySaveRecordAsync(po, ct);
        if (!string.IsNullOrWhiteSpace(doc.SupplierName))
        {
            try { await _repo.SaveVendorAsync(doc.SupplierName, vendor.CardCode, vendor.CardName, user, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Vendor choice for supplier {Supplier} not remembered.", doc.SupplierName); }
        }
        await _activity.RecordAsync(documentId, "purchase_order",
            $"Purchase order {resp.DocNum} created in SAP for {vendor.CardCode}: {preview.Lines.Count} line(s), " +
            $"{preview.DocTotal:N2} {PoCurrency}, warehouse {preview.Warehouse}.", user, ct);

        _logger.LogInformation(
            "Autohub PO created for invoice {DocId}: DocEntry={DocEntry}, DocNum={DocNum}, vendor={CardCode}, lines={Lines}, total={Total} TZS.",
            documentId, resp.DocEntry, resp.DocNum, vendor.CardCode, preview.Lines.Count, preview.DocTotal);
        return new(200, po, false, Array.Empty<string>());
    }

    // The SAP purchase order exists either way (U_AppRef keeps a retry from doubling it); a failed Neon
    // write only loses the page's shortcut, so it is logged, never fatal.
    private async Task TrySaveRecordAsync(AutohubPurchaseOrderRecord record, CancellationToken ct)
    {
        try { await _repo.SavePurchaseOrderAsync(record, ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PO {DocNum} of invoice {DocId} not recorded in Neon.", record.DocNum, record.DocumentId);
        }
    }

    /// <summary>OPOR.Comments: "Autohub invoice INV-1 of 27.09.2026 · SUPPLIER" (254 characters at most).</summary>
    internal static string BuildComments(StagingPartsDocumentRow doc)
    {
        var text = "Autohub invoice " + (doc.InvoiceNumber?.Trim() ?? doc.OriginalFilename)
            + (doc.InvoiceDate is { } d ? $" of {d:dd.MM.yyyy}" : "")
            + (string.IsNullOrWhiteSpace(doc.SupplierName) ? "" : $" · {doc.SupplierName.Trim()}");
        return Clip(text, 254);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Join(IReadOnlyList<string> codes) =>
        string.Join(", ", codes.Take(15)) + (codes.Count > 15 ? $" and {codes.Count - 15} more" : "");
}
