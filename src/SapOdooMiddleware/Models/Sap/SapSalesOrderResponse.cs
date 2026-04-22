namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Response returned after successfully creating a Sales Order in SAP B1.
/// </summary>
public class SapSalesOrderResponse
{
    /// <summary>SAP Sales Order DocEntry (internal key).</summary>
    public int DocEntry { get; set; }

    /// <summary>SAP Sales Order DocNum (user-facing number).</summary>
    public int DocNum { get; set; }

    /// <summary>
    /// The Odoo SO identifier written onto the SAP SO header UDF <c>U_Odoo_SO_ID</c>
    /// and <c>NumAtCard</c>.
    /// </summary>
    public string UOdooSoId { get; set; } = string.Empty;

    /// <summary>Pick list absolute entry, if a pick list was auto-created.</summary>
    public int? PickListEntry { get; set; }

    /// <summary>
    /// Optional human-readable caveat describing a condition the
    /// caller (ICC) should surface to operators even though the SO
    /// operation itself succeeded.  Populated for example when an
    /// update to an already-synced SO added new lines to ORDR/RDR1,
    /// but the existing pick list (OPKL) could not be refreshed
    /// because it is already Picked or Closed — warehouse staff need
    /// to either add the missing line(s) to the active pick list or
    /// open a new pick list for the additional items.
    /// </summary>
    public string? Warning { get; set; }

    /// <summary>
    /// Machine-readable per-line caveats.  Each entry describes a
    /// condition the caller (ICC) should surface to operators while
    /// the SO operation itself succeeded.  The SO is always posted
    /// to SAP regardless of Warnings; pick-list lines that can't be
    /// fully allocated against bin stock are reported here so the
    /// warehouse can intervene manually.
    /// </summary>
    public List<SalesOrderWarning> Warnings { get; set; } = new();
}

/// <summary>
/// Structured warning about one sales-order condition the caller
/// should surface to operators.
/// </summary>
public class SalesOrderWarning
{
    /// <summary>Stable machine-readable code so callers can branch
    /// on it without string-matching prose.  Known codes:
    /// <list type="bullet">
    ///   <item><c>BIN_SHORTFALL</c> — a pick-list line couldn't be
    ///     fully covered by bin stock in its warehouse; the SO line
    ///     was posted but skipped on the pick list.</item>
    /// </list>
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>SAP item code for the affected line, when applicable.</summary>
    public string? ItemCode { get; set; }

    /// <summary>Position in the original request.Lines array (zero-based).</summary>
    public int? LineNum { get; set; }

    /// <summary>SAP warehouse the SO line was posted to.</summary>
    public string? WarehouseCode { get; set; }

    /// <summary>Requested quantity on the line.</summary>
    public double? Required { get; set; }

    /// <summary>Quantity actually allocated across bins (may be partial).</summary>
    public double? Allocated { get; set; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; set; } = string.Empty;
}
