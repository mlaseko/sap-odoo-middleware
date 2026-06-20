namespace SapOdooMiddleware.Services.Autohub.Excel;

/// <summary>
/// The single source of truth for the Autohub Excel invoice template layout: sheet name, the
/// document-metadata block, the line-table header row, the allowed dropdown values, and the known
/// supplier SKU prefixes. Both <see cref="ExcelTemplateGenerator"/> (which builds the blank workbook)
/// and <see cref="ExcelInvoiceParser"/> (which reads a filled one) reference these constants, so the
/// template a user downloads can never drift from what the parser expects.
/// </summary>
public static class ExcelTemplateSchema
{
    public const string TemplateFileName = "MolasAutohub_Invoice_Template_v1.xlsx";
    public const string SheetName        = "Invoice Lines";

    // ---- Document metadata block: label in column A, value in column B, rows 1-7. ----
    public const int MetaSupplierRow   = 1;
    public const int MetaInvoiceNoRow  = 2;
    public const int MetaInvoiceDateRow = 3;
    public const int MetaCurrencyRow   = 4;
    public const int MetaTotalRow      = 5;
    public const int MetaForexRateRow  = 6;   // optional
    public const int MetaForexDateRow  = 7;   // optional
    public const int MetaLabelCol      = 1;   // column A
    public const int MetaValueCol      = 2;   // column B

    // ---- Line table: header on row 9, data from row 10. ----
    public const int HeaderRow    = 9;
    public const int FirstDataRow = 10;

    // Column indices (1-based) for the line table.
    public const int ColLineNumber = 1;   // A
    public const int ColSku        = 2;   // B
    public const int ColOem        = 3;   // C
    public const int ColDescription = 4;  // D
    public const int ColBrand      = 5;   // E
    public const int ColQuantity   = 6;   // F
    public const int ColUnit       = 7;   // G
    public const int ColUnitPrice  = 8;   // H
    public const int ColDiscount   = 9;   // I
    public const int ColLineTotal  = 10;  // J
    public const int ColPromotional = 11; // K
    public const int ColNotes      = 12;  // L
    public const int LastColumn    = ColNotes;

    /// <summary>Header labels in column order (A..L). Parser verifies these are intact (rename = error).</summary>
    public static readonly string[] ColumnHeaders =
    {
        "LineNumber", "SupplierArticleNumber", "OemNumbers", "Description", "Brand", "Quantity",
        "Unit", "UnitPriceForeign", "DiscountPct", "LineTotalForeign", "IsPromotional", "Notes",
    };

    /// <summary>Allowed invoice currencies (matches the forex_rate currency CHECK constraint).</summary>
    public static readonly string[] Currencies = { "USD", "AED", "GBP", "EUR" };

    /// <summary>Allowed units (Excel dropdown).</summary>
    public static readonly string[] Units = { "Piece", "Set", "Litre", "Kit", "Pack", "Box", "Each" };

    /// <summary>
    /// Known supplier SKU prefixes. A SKU-column value that looks like an OEM number but does NOT start
    /// with one of these is treated as an OEM that bled into the SKU column (see <c>LineValidator</c>).
    /// </summary>
    public static readonly string[] KnownSkuPrefixes =
        { "GL", "GLR", "GLD", "GLW", "GLEV", "GLRV", "GW", "TAN", "VK" };
}
