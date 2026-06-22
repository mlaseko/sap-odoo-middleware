using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Services.Vision;

/// <summary>
/// Vision extractor for spare-parts invoices. Mirrors <see cref="IInvoiceExtractor"/> but targets
/// the Autohub DGX endpoint (/extract_parts_invoice) and a parts-specific DTO shape (OEM arrays,
/// supplier article number, per-line brand, foreign-currency prices).
/// </summary>
public interface IInvoicePartsExtractor
{
    Task<PartsInvoicePageExtraction> ExtractPageAsync(byte[] pngBytes, int pageNo, CancellationToken ct);
}

public sealed record PartsInvoicePageExtraction
{
    [JsonPropertyName("header")] public PartsInvoiceHeader?    Header { get; init; }
    [JsonPropertyName("lines")]  public List<PartsInvoiceLine> Lines  { get; init; } = new();
    [JsonPropertyName("error")]  public string?               Error  { get; init; }
}

public sealed record PartsInvoiceHeader
{
    [JsonPropertyName("supplier_name")]  public string?  SupplierName  { get; init; }
    [JsonPropertyName("invoice_number")] public string?  InvoiceNumber { get; init; }
    [JsonPropertyName("invoice_date")]   public string?  InvoiceDate   { get; init; }  // yyyy-MM-dd, parsed server-side
    [JsonPropertyName("currency")]       public string?  Currency      { get; init; }
    [JsonPropertyName("total_amount")]   public decimal? TotalAmount   { get; init; }  // grand total, last page only
}

public sealed record PartsInvoiceLine
{
    [JsonPropertyName("supplier_article_number")] public string?       SupplierArticleNumber { get; init; }
    [JsonPropertyName("oem_numbers")]             public List<string>? OemNumbers            { get; init; }
    [JsonPropertyName("description")]             public string?       Description           { get; init; }
    [JsonPropertyName("brand")]                   public string?       Brand                 { get; init; }
    [JsonPropertyName("quantity")]                public decimal?      Quantity              { get; init; }
    [JsonPropertyName("unit")]                    public string?       Unit                  { get; init; }
    [JsonPropertyName("unit_price_foreign")]      public decimal?      UnitPriceForeign      { get; init; }
    [JsonPropertyName("discount_pct")]            public decimal?      DiscountPct           { get; init; }
    [JsonPropertyName("line_total_foreign")]      public decimal?      LineTotalForeign      { get; init; }
}
