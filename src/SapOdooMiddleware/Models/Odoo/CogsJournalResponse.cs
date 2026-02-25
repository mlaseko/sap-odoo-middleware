namespace SapOdooMiddleware.Models.Odoo;

/// <summary>
/// Response returned after creating or updating a COGS journal entry in Odoo.
/// </summary>
public class CogsJournalResponse
{
    /// <summary>SAP AR Invoice DocEntry that triggered this COGS entry.</summary>
    public int SapDocEntry { get; set; }

    /// <summary>Odoo account.move ID of the customer invoice this COGS entry is linked to.</summary>
    public int OdooInvoiceId { get; set; }

    /// <summary>Odoo invoice name (e.g. "INV/2026/00001").</summary>
    public string OdooInvoiceName { get; set; } = string.Empty;

    /// <summary>Odoo account.move ID of the COGS journal entry (created or updated).</summary>
    public int CogsJournalEntryId { get; set; }

    /// <summary>
    /// Action taken: "created", "updated", or "skipped" (same hash → no change needed).
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>SHA-256 hash of the COGS payload used for idempotency.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Number of COGS debit lines created in the journal entry.</summary>
    public int DebitLineCount { get; set; }

    /// <summary>Total COGS amount (sum of all line costs).</summary>
    public double TotalCogs { get; set; }
}
