namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request to create a spare-parts item master in SAP B1 (Molas Autohub / MOLAS_Live_2021).
/// Prices are TZS and map to the confirmed Autohub price lists: Cost→PL01, Retail→PL03,
/// Wholesale→PL05. The U_ fields land on the actual MOLAS_Live_2021 OITM UDFs:
///   ItemName (standard) = OEMs + article joined by '/'
///   U_Item_Name         = part description (e.g. "BRAKE PADS")
///   U_Article_No        = supplier article (also the Tier-2 match key)
///   U_Engine_code       = supplier article (per the company's convention)
///   U_ItemManufacturer  = brand/supplier (e.g. GERMAX)
///   U_MdlTEST           = brand/supplier (mirrors U_ItemManufacturer)
/// </summary>
public record SapAutohubItemRequest(
    string  ItemCode,
    string  ItemName,         // standard OITM ItemName = OEMs + article joined by '/'
    int     ItemsGroupCode,
    decimal CostPrice,        // PriceList 1
    decimal RetailPrice,      // PriceList 3
    decimal WholesalePrice,   // PriceList 5
    string  ArticleNumber,    // U_Article_No + U_Engine_code (also the Tier-2 match key)
    string? PartName,         // U_Item_Name
    string? Manufacturer);    // U_ItemManufacturer + U_MdlTEST
