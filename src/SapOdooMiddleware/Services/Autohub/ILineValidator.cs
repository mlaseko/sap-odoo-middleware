using SapOdooMiddleware.Services.Vision;

namespace SapOdooMiddleware.Services.Autohub;

/// <summary>One sanitisation/quality finding on a single extracted or imported line.</summary>
/// <param name="Field">The line field the finding relates to (e.g. SupplierArticleNumber, Quantity).</param>
/// <param name="Code">A stable machine code, e.g. <c>sku_too_short</c>, <c>qty_recovered_from_arithmetic</c>.</param>
public sealed record LineValidationIssue(string Field, string Code);

/// <summary>The sanitised line plus the list of findings produced while sanitising it.</summary>
public sealed record LineValidationResult(PartsInvoiceLine Line, IReadOnlyList<LineValidationIssue> Issues);

/// <summary>
/// Shared, deterministic line validator applied to BOTH ingestion paths (the DGX vision response and
/// the Excel import) so a line behaves identically regardless of how it arrived. It only ever nulls
/// obviously-broken values or recovers a value from the line arithmetic — it never fabricates data, so
/// the worst case is a line routed to manual review rather than corrupt data flowing into SAP. Mirrors
/// the DGX-side <c>_validate_parts_extraction</c> rules; if the two drift, that is a bug.
/// </summary>
public interface ILineValidator
{
    LineValidationResult Validate(PartsInvoiceLine line);
}
