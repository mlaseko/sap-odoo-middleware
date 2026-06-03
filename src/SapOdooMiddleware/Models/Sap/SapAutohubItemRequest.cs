namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request to create a spare-parts item master in SAP B1 (Molas Autohub).
/// Prices are TZS and map to the confirmed Autohub price lists: Cost→PL01, Retail→PL03,
/// Wholesale→PL05 (no dealer/super-dealer tiers). The U_ fields land on existing OITM UDFs.
/// </summary>
public record SapAutohubItemRequest(
    string  ItemCode,
    string  ItemName,
    int     ItemsGroupCode,
    decimal CostPrice,        // PriceList 1
    decimal RetailPrice,      // PriceList 3
    decimal WholesalePrice,   // PriceList 5
    string  ArticleNumber,    // U_Article_No (also the Tier-2 match key)
    string? Description,      // U_Description
    string? FitForAuto,       // U_FitForAuto
    string? ImageUrl);        // U_ImageUrl
