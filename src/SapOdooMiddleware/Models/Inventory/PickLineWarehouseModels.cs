using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Inventory;

// ── Request ──────────────────────────────────────────────────────────

/// <summary>
/// Re-sources one RELEASED, unpicked pick-list line to another warehouse by
/// updating the underlying sales-order line's WarehouseCode. SAP propagates
/// the change onto the existing pick list in place (same AbsEntry) — verified
/// against MOLAS_Live_2021; the response reports whether the pick line
/// actually followed.
/// </summary>
public class PickLineWarehouseChange
{
    [JsonPropertyName("app_ref")]
    public string AppRef { get; set; } = "";

    /// <summary>Shown in logs; the app sends the acting user's display name.</summary>
    [JsonPropertyName("changed_by")]
    public string ChangedBy { get; set; } = "";

    [JsonPropertyName("pick_entry")]
    public int PickEntry { get; set; }

    /// <summary>Target warehouse code.</summary>
    [JsonPropertyName("whs_code")]
    public string WhsCode { get; set; } = "";
}

// ── Validated plan (planner → DI API) ────────────────────────────────

public class PickLineWarehouseChangePlan
{
    public List<string> Errors { get; } = new();
    /// <summary>True when the line already sits in the requested warehouse.</summary>
    public bool AlreadyApplied { get; set; }
    public int OrderEntry { get; set; }
    public int OrderLine { get; set; }
    public int PickEntry { get; set; }
    public string ItemCode { get; set; } = "";
    public string SourceWhs { get; set; } = "";
    public string TargetWhs { get; set; } = "";
}

// ── Result ───────────────────────────────────────────────────────────

public class PickLineWarehouseResult
{
    [JsonPropertyName("abs_entry")]
    public int AbsEntry { get; set; }

    [JsonPropertyName("pick_entry")]
    public int PickEntry { get; set; }

    [JsonPropertyName("order_entry")]
    public int OrderEntry { get; set; }

    [JsonPropertyName("order_line")]
    public int OrderLine { get; set; }

    [JsonPropertyName("item_code")]
    public string ItemCode { get; set; } = "";

    [JsonPropertyName("old_whs_code")]
    public string OldWhsCode { get; set; } = "";

    [JsonPropertyName("new_whs_code")]
    public string NewWhsCode { get; set; } = "";

    [JsonPropertyName("already_applied")]
    public bool AlreadyApplied { get; set; }

    /// <summary>
    /// True when the pick-list line shows the new warehouse right after the
    /// sales-order update. False means the SO changed but the pick line has
    /// not followed (yet) — surfaced so the operator can check in SAP.
    /// </summary>
    [JsonPropertyName("pick_line_followed")]
    public bool PickLineFollowed { get; set; }
}
