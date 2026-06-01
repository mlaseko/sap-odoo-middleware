using System.Text.Json.Serialization;

namespace SapOdooMiddleware.Services.Vision;

public record InvoicePageExtraction
{
    [JsonPropertyName("header")] public InvoiceHeader?     Header { get; init; }
    [JsonPropertyName("lines")]  public List<InvoiceLine>  Lines  { get; init; } = new();
    [JsonPropertyName("footer")] public InvoiceFooter?     Footer { get; init; }
    [JsonPropertyName("error")]  public string?            Error  { get; init; }
}

public record InvoiceHeader
{
    [JsonPropertyName("invoice_number")]    public string?  InvoiceNumber   { get; init; }
    [JsonPropertyName("invoice_date")]      public string?  InvoiceDate     { get; init; } // yyyy-MM-dd, parse server-side
    [JsonPropertyName("sales_order")]       public string?  SalesOrder      { get; init; }
    [JsonPropertyName("delivery_note_ref")] public string?  DeliveryNoteRef { get; init; }
    [JsonPropertyName("customer_name")]     public string?  CustomerName    { get; init; }
    [JsonPropertyName("customer_account")]  public string?  CustomerAccount { get; init; }
    [JsonPropertyName("currency")]          public string?  Currency        { get; init; }
}

public record InvoiceLine
{
    [JsonPropertyName("article_number")] public string?  ArticleNumber { get; init; }
    [JsonPropertyName("description")]    public string?  Description   { get; init; }
    [JsonPropertyName("pack_size")]      public string?  PackSize      { get; init; }
    [JsonPropertyName("unit_price")]     public decimal? UnitPrice     { get; init; }
    [JsonPropertyName("quantity")]       public decimal? Quantity      { get; init; }
    [JsonPropertyName("unit")]           public string?  Unit          { get; init; }
    [JsonPropertyName("commodity_code")] public string?  CommodityCode { get; init; }
    [JsonPropertyName("origin")]         public string?  Origin        { get; init; }
    [JsonPropertyName("discount_pct")]   public decimal  DiscountPct   { get; init; }
    [JsonPropertyName("line_total")]     public decimal? LineTotal     { get; init; }
}

public record InvoiceFooter
{
    [JsonPropertyName("subtotal")]       public decimal? Subtotal     { get; init; }
    [JsonPropertyName("freight")]        public decimal? Freight      { get; init; }
    [JsonPropertyName("total_net")]      public decimal? TotalNet     { get; init; }
    [JsonPropertyName("tax_amount")]     public decimal? TaxAmount    { get; init; }
    [JsonPropertyName("invoice_total")]  public decimal? InvoiceTotal { get; init; }
    [JsonPropertyName("payment_terms")]  public string?  PaymentTerms { get; init; }
    [JsonPropertyName("due_date")]       public string?  DueDate      { get; init; } // yyyy-MM-dd
}
