namespace MolasLubes.Infrastructure.Integrations.LiquiMoly;

public class LiquiMolyProductDto
{
    // ─── Identity ──────────────────────────────────────────────────────────────
    public string  ArticleNumber { get; set; } = null!;
    public string  Name          { get; set; } = null!;
    public string? ProductUrl    { get; set; }

    // ─── Classification ────────────────────────────────────────────────────────
    public string? Category      { get; set; }
    public string? SubCategory   { get; set; }

    // ─── Product detail ────────────────────────────────────────────────────────
    public string? Description   { get; set; }

    /// <summary>Primary packaging size extracted from the product name (e.g. "5 L").</summary>
    public string? PackagingSize { get; set; }

    /// <summary>All available packaging/volume variants scraped from the product page.</summary>
    public List<string> AllPackagingSizes { get; set; } = new();

    /// <summary>Volume in litres parsed from PackagingSize (e.g. 5.0, 0.5 for 500 ml). Null for weight-only units.</summary>
    public decimal? Liter { get; set; }

    /// <summary>Primary spec grade extracted from the product name (e.g. "5W-30").</summary>
    public string? SpecGrade { get; set; }

    // ─── Media ─────────────────────────────────────────────────────────────────
    /// <summary>Primary product image URL.</summary>
    public string? ImageUrl { get; set; }

    /// <summary>All product image URLs scraped from the product detail page gallery.</summary>
    public List<string> AllImageUrls { get; set; } = new();

    // —— Barcode / SAP UoM snapshot ———————————————————————————————————————————————
    /// <summary>Primary SAP barcode selected for this article.</summary>
    public string? PrimaryBarcode { get; set; }

    /// <summary>UoM code of the selected primary barcode (e.g. "Unit", "4-PU").</summary>
    public string? PrimaryBarcodeUomCode { get; set; }

    /// <summary>Human-readable UoM name of the selected primary barcode.</summary>
    public string? PrimaryBarcodeUomName { get; set; }

    /// <summary>SAP UoM entry for the selected primary barcode.</summary>
    public int? PrimaryBarcodeUomEntry { get; set; }

    /// <summary>Base quantity inside the SAP UoM group for the selected primary barcode.</summary>
    public decimal? PrimaryBarcodeBaseQtyInGroup { get; set; }

    /// <summary>True when SAP has a Unit barcode row for this article.</summary>
    public bool HasUnitBarcode { get; set; }

    /// <summary>How the primary barcode was resolved from SAP barcode rows.</summary>
    public string? BarcodeResolutionStatus { get; set; }

    /// <summary>Optional explanation for fallback/ambiguous barcode selections.</summary>
    public string? BarcodeResolutionNote { get; set; }

    /// <summary>All SAP barcode rows captured for this article.</summary>
    public List<LiquiMolyBarcodeRowDto> AllBarcodes { get; set; } = new();

    /// <summary>Read-only SAP item/UoM snapshot used for diagnostics and API display.</summary>
    public LiquiMolySapUomInfoDto? SapUomInfo { get; set; }

    // ─── Approvals & Specifications ────────────────────────────────────────────
    /// <summary>
    /// Full list of OEM / industry approvals (e.g. "BMW Longlife-04", "MB-Approval 229.51").
    /// Each entry is one approval text as listed on the product page.
    /// </summary>
    public List<string> Approvals { get; set; } = new();

    /// <summary>
    /// Key-value technical specifications as shown in the "Specifications" table
    /// on the product page when available.
    /// Kept for backward compatibility with older consumers.
    /// </summary>
    public Dictionary<string, string> Specifications { get; set; } = new();

    /// <summary>
    /// Plain list of specification items from the "Specifications / Approvals" section
    /// (e.g. "ACEA C3", "API SP").
    /// </summary>
    public List<string> SpecificationItems { get; set; } = new();

    /// <summary>
    /// Bullet-point overview properties / benefits from the product detail page.
    /// Stored in scrape order.
    /// </summary>
    public List<string> OverviewProperties { get; set; } = new();

    /// <summary>
    /// Product application / usage instructions from the "Application" section.
    /// </summary>
    public string? Application { get; set; }

    /// <summary>
    /// LIQUI MOLY recommendation items from the "LIQUI MOLY recommends" section.
    /// </summary>
    public List<string> LiquiMolyRecommendations { get; set; } = new();

    // ─── Downloads ─────────────────────────────────────────────────────────────
    /// <summary>Direct URL to the English Production / Product Information PDF.</summary>
    public string? ProductInfoPdfUrl { get; set; }

    /// <summary>Direct URL to the English Safety Data Sheet PDF.</summary>
    public string? SafetyDataSheetPdfUrl { get; set; }

    // Optional if you want:
    public string? SpecificationsText { get; set; }

    /// <summary>When this row was last scraped/refreshed (NeonLiquiMolyProducts.ScrapedAt).</summary>
    public DateTime? ScrapedAt { get; set; }


}

public class LiquiMolyBarcodeRowDto
{
    public string Code { get; set; } = string.Empty;
    public string? UomCode { get; set; }
    public string? UomName { get; set; }
    public int? UomEntry { get; set; }
    public decimal? BaseQtyInGroup { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsUnit { get; set; }
}

public class LiquiMolySapUomInfoDto
{
    public int? UomGroupEntry { get; set; }
    public string? UomGroupName { get; set; }
    public string? DefaultCountingUomName { get; set; }
    public string? InventoryUomName { get; set; }
    public string? SalesUomName { get; set; }
    public string? PurchaseUomName { get; set; }
}
