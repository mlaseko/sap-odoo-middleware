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

    /// <summary>Prices keyed by SAP price list name: PL01 … PL05 (TZS).</summary>
    [JsonPropertyName("prices")] public Dictionary<string, decimal>? Prices { get; set; }
}

/// <summary>PATCH /api/sap/items/{itemCode} body — only these fields are editable.</summary>
public class SapItemUpdateApiRequest
{
    [JsonPropertyName("itemName")] public string? ItemName { get; set; }
    [JsonPropertyName("U_MdlTEST")] public string? UMdlTest { get; set; }
    [JsonPropertyName("U_Item_Name")] public string? UItemName { get; set; }
    [JsonPropertyName("U_Article_No")] public string? UArticleNo { get; set; }
}
