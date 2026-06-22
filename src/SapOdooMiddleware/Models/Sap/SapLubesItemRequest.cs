namespace SapOdooMiddleware.Models.Sap;

/// <summary>
/// Request to create a Liqui Moly "Lubes" item master in SAP B1.
/// Prices are NET (excl-VAT) TZS values destined for price lists 1/2/3.
/// </summary>
public record SapLubesItemRequest(
    string  ItemCode,
    string  ItemName,
    int     ItemsGroupCode,
    decimal RetailNetPrice,        // PriceList 1
    decimal DealerNetPrice,        // PriceList 2
    decimal SuperDealerNetPrice,   // PriceList 3
    string  OdooCategoryName);
