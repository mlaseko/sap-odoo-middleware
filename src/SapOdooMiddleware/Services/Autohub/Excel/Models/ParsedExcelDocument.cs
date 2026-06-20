using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Services.Autohub.Excel.Models;

/// <summary>
/// One parsed data row: the line mapped to the common DTO, the source Excel row number (for warning/
/// error reporting), and the operator's explicit IsPromotional flag from column K (OR-ed with the
/// derived <c>PartsPromotionRules</c> result when the row is persisted).
/// </summary>
public sealed record ParsedExcelLine(int ExcelRow, PartsInvoiceLine Line, bool ExplicitPromotional);

/// <summary>
/// A fully parsed Excel invoice: the document header (mapped to the same shape the vision path emits),
/// the optional forex hints (audit-only — not written to the staging document, to mirror the PDF path),
/// and the line rows in sheet order.
/// </summary>
public sealed record ParsedExcelDocument(
    PartsInvoiceHeader Header,
    decimal? ForexRateUsed,
    DateTime? ForexRateDate,
    IReadOnlyList<ParsedExcelLine> Lines);

/// <summary>A hard parse/validation failure tied to a cell. Any of these aborts the whole import (400).</summary>
public sealed record ExcelParseError(int Row, string Field, string Issue);

/// <summary>Outcome of parsing: either a document (no hard errors) or the list of hard errors.</summary>
public sealed record ExcelParseResult(ParsedExcelDocument? Document, IReadOnlyList<ExcelParseError> HardErrors)
{
    public bool Ok => HardErrors.Count == 0 && Document is not null;

    public static ExcelParseResult Fail(IReadOnlyList<ExcelParseError> errors) => new(null, errors);
    public static ExcelParseResult Fail(ExcelParseError error) => new(null, new[] { error });
    public static ExcelParseResult Success(ParsedExcelDocument doc) => new(doc, Array.Empty<ExcelParseError>());
}
