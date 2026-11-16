using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SapOdooMiddleware.Persistence;

namespace SapOdooMiddleware.Areas.Autohub.Pages.Documents;

public class DetailModel : PageModel
{
    private readonly IStagingPartsDocumentRepository _docs;
    private readonly IPartsReviewRepository _review;

    public DetailModel(IStagingPartsDocumentRepository docs, IPartsReviewRepository review,
        Microsoft.Extensions.Options.IOptions<SapOdooMiddleware.Configuration.AutohubPricingSettings> pricing)
    {
        _docs = docs;
        _review = review;
        RequirePriceReview = pricing.Value.RequirePriceReview;
    }

    /// <summary>AutohubPricing:RequirePriceReview — Bulk Create creates only price-approved lines.</summary>
    public bool RequirePriceReview { get; }

    public StagingPartsDocumentRow Doc { get; private set; } = default!;
    public IReadOnlyList<PartsReviewLineRow> Lines { get; private set; } = Array.Empty<PartsReviewLineRow>();

    /// <summary>Review UI shows once extracted (and stays visible, read-only, after review).</summary>
    public bool ShowReview => Doc.Status is "extracted" or "reviewed";
    public bool IsReviewed => Doc.Status == "reviewed";
    public bool IsEditable => Doc.Status == "extracted";

    /// <summary>Excel documents can be re-extracted in place (re-parse the stored .xlsx, refresh lines).</summary>
    public bool IsExcel => Doc.OriginalFilename?.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) == true;
    public bool CanReExtract => IsExcel && IsEditable;

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        var doc = await _docs.GetByIdAsync(id, ct);
        if (doc is null) return NotFound();

        Doc = doc;
        Lines = await _review.ListByDocumentAsync(id, ct);
        ViewData["Title"] = doc.InvoiceNumber ?? doc.OriginalFilename;
        return Page();
    }

    public int Count(string status) => Lines.Count(l => l.ReviewStatus == status);

    public static (string Css, string Text) LinePill(string status) => status switch
    {
        "matched"       => ("pill-green", "matched"),
        "created"       => ("pill-green", "created"),
        "create_new"    => ("pill-blue", "create new"),
        "skip"          => ("pill-grey", "skip"),
        "create_failed" => ("pill-red", "create failed"),
        "needs_manual"  => ("pill-orange", "needs manual"),
        "needs_manufacturer" => ("pill-orange", "assign marque"),
        "prefix_exhausted"   => ("pill-red", "SKU range full"),
        "needs_confirmation" => ("pill-purple", "confirm match"),
        _               => ("pill-yellow", "pending"),
    };

    /// <summary>
    /// Tooltip for the "⚠ check part" badge: the DGX could not confirm the matched part is the same kind of
    /// part as the invoice line (part-name comparison verdict MATCH / UNCERTAIN / DIFFERENT).
    /// </summary>
    public static string ReviewReason(string? verdict, string? selectedName, string? selectedSupplier)
    {
        var why = string.Equals(verdict, "DIFFERENT", StringComparison.OrdinalIgnoreCase)
            ? "The matched part looks like a different kind of part from the invoice line."
            : "The matched part could not be confirmed as the same kind of part as the invoice line.";
        var part = string.IsNullOrWhiteSpace(selectedName) ? "" :
            $" Matched part: {selectedName}{(string.IsNullOrWhiteSpace(selectedSupplier) ? "" : $" ({selectedSupplier})")}.";
        return why + part + " Open the line to confirm it or choose another part.";
    }

    /// <summary>Plain-language explanation for the source badge (tooltip).</summary>
    public static string SourceExplanation(string? matchStrategy)
    {
        var s = matchStrategy ?? "";
        if (s.Contains("cross_supplier")) return "Matched to another supplier's part (same OEM number); the item is created under this invoice's supplier.";
        if (s.Contains("germax"))          return "Found in the Germax catalogue by a shared OEM number.";
        if (s.Contains("rapidapi"))        return "Found by a live TecDoc search on the OEM / article number.";
        if (s.Contains("borrowed_oem_bridge")) return "Borrowed from another supplier's TecDoc part with the same OEM number.";
        if (s is "tier1_oem" or "tier2_article") return "Already in SAP (matched by OEM or article number in the SAP mirror).";
        if (s.Contains("tecdoc") || s.Contains("enrichment_direct")) return "Found in TecDoc by this supplier's article number.";
        return s;
    }

    /// <summary>Small "where did this match come from" badge derived from MatchStrategy (Slice 2). Empty = no badge.</summary>
    public static (string Label, string Css) SourceBadge(string? matchStrategy)
    {
        var s = matchStrategy ?? "";
        if (s.Contains("cross_supplier"))                          return ("Cross-supplier", "pill-grey");
        if (s.Contains("germax"))                                  return ("Germax", "pill-purple");
        if (s.Contains("rapidapi"))                               return ("RapidAPI", "pill-orange");
        if (s.Contains("borrowed_oem_bridge"))                    return ("OEM bridge", "pill-blue");
        if (s is "tier1_oem" or "tier2_article")                  return ("SAP mirror", "pill-green");
        if (s.Contains("tecdoc") || s.Contains("enrichment_direct")) return ("TecDoc", "pill-green");
        return ("", "");
    }

    public (string Css, string Text) ValidationBadge()
    {
        if (Doc.Status == "failed") return ("badge-red", "failed");
        return Doc.ValidationStatus switch
        {
            "ok"                     => ("badge-green", "ok"),
            "totals_mismatch"        => ("badge-yellow", "totals mismatch"),
            "missing_currency"       => ("badge-yellow", "missing currency"),
            "missing_supplier"       => ("badge-yellow", "missing supplier"),
            "missing_invoice_number" => ("badge-yellow", "missing invoice #"),
            "no_lines"               => ("badge-grey", "no lines"),
            null                     => ("badge-grey", Doc.Status),
            _                        => ("badge-grey", Doc.ValidationStatus!)
        };
    }

    /// <summary>Currency symbol/prefix for amount display.</summary>
    public string Sym() => (Doc.Currency ?? "").ToUpperInvariant() switch
    {
        "USD" => "$",
        "EUR" => "€",
        "GBP" => "£",
        "AED" => "AED ",
        ""    => "",
        _     => Doc.Currency + " "
    };
}
