using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Models.Sap;

// DTOs for the Autohub Item Master API (/api/sap/...). JsonPropertyName pins the
// wire contract to the agreed spec (camelCase + literal SAP UDF names), overriding
// the app-wide snake_case policy.

/// <summary>One SAP Item Group for the frontend dropdown.</summary>
public record SapItemGroupDto(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("name")] string Name);

/// <summary>POST /api/sap/items body.</summary>
public class SapItemCreateApiRequest
{
    [JsonPropertyName("itemCode")] public string ItemCode { get; set; } = "";
    [JsonPropertyName("itemName")] public string ItemName { get; set; } = "";
    [JsonPropertyName("itemGroupCode")] public int ItemGroupCode { get; set; }

    [JsonPropertyName("U_MdlTEST")] public string? UMdlTest { get; set; }
    [JsonPropertyName("U_Item_Name")] public string? UItemName { get; set; }
    [JsonPropertyName("U_Article_No")] public string? UArticleNo { get; set; }
    [JsonPropertyName("U_OE_Numbers")] public string? UOeNumbers { get; set; }

    /// <summary>Prices for SAP price lists 1-5 (TZS). Omitted lists are left at 0.</summary>
    [JsonPropertyName("prices")] public SapItemPricesDto? Prices { get; set; }
}

/// <summary>
/// TZS prices per SAP price list (ITM1 rows, written via the DI API's PriceList
/// collection selected by ListNum). Explicit properties so Swagger shows the real
/// keys instead of dictionary placeholders.
/// </summary>
public class SapItemPricesDto
{
    [JsonPropertyName("PL01")] public decimal? PL01 { get; set; }
    [JsonPropertyName("PL02")] public decimal? PL02 { get; set; }
    [JsonPropertyName("PL03")] public decimal? PL03 { get; set; }
    [JsonPropertyName("PL04")] public decimal? PL04 { get; set; }
    [JsonPropertyName("PL05")] public decimal? PL05 { get; set; }

    /// <summary>ListNum (1-5) → price, for the provided values only.</summary>
    public Dictionary<int, decimal> ToListNumMap()
    {
        var map = new Dictionary<int, decimal>();
        if (PL01 is { } p1) map[1] = p1;
        if (PL02 is { } p2) map[2] = p2;
        if (PL03 is { } p3) map[3] = p3;
        if (PL04 is { } p4) map[4] = p4;
        if (PL05 is { } p5) map[5] = p5;
        return map;
    }
}

/// <summary>PATCH /api/sap/items/{itemCode} body — only these fields are editable.</summary>
public class SapItemUpdateApiRequest
{
    [JsonPropertyName("itemName")] public string? ItemName { get; set; }
    [JsonPropertyName("U_MdlTEST")] public string? UMdlTest { get; set; }
    [JsonPropertyName("U_Item_Name")] public string? UItemName { get; set; }
    [JsonPropertyName("U_Article_No")] public string? UArticleNo { get; set; }
}

/// <summary>Prices per list (PL01-PL05) as read from ITM1; a missing list is null.</summary>
public class SapItemPricesReadDto
{
    [JsonPropertyName("PL01")] public decimal? PL01 { get; set; }
    [JsonPropertyName("PL02")] public decimal? PL02 { get; set; }
    [JsonPropertyName("PL03")] public decimal? PL03 { get; set; }
    [JsonPropertyName("PL04")] public decimal? PL04 { get; set; }
    [JsonPropertyName("PL05")] public decimal? PL05 { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
}

/// <summary>
/// GET /api/sap/items/{itemCode} response — the live OITM row with its group,
/// the four editable UDFs plus U_OE_Numbers, flags, total on-hand and prices.
/// Read by direct SQL (no DI API seat), so it is safe to call on every page view.
/// </summary>
public class SapItemMasterDto
{
    [JsonPropertyName("item_code")] public string ItemCode { get; set; } = "";
    [JsonPropertyName("item_name")] public string ItemName { get; set; } = "";
    [JsonPropertyName("item_group_code")] public int? ItemGroupCode { get; set; }
    [JsonPropertyName("item_group_name")] public string? ItemGroupName { get; set; }
    [JsonPropertyName("U_MdlTEST")] public string? UMdlTest { get; set; }
    [JsonPropertyName("U_Item_Name")] public string? UItemName { get; set; }
    [JsonPropertyName("U_Article_No")] public string? UArticleNo { get; set; }
    [JsonPropertyName("U_OE_Numbers")] public string? UOeNumbers { get; set; }
    /// <summary>OITM.validFor = 'Y'.</summary>
    [JsonPropertyName("active")] public bool Active { get; set; }
    /// <summary>OITM.frozenFor = 'Y'.</summary>
    [JsonPropertyName("frozen")] public bool Frozen { get; set; }
    [JsonPropertyName("on_hand")] public decimal OnHand { get; set; }
    [JsonPropertyName("prices")] public SapItemPricesReadDto Prices { get; set; } = new();
    [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
}

