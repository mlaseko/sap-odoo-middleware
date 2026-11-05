using System.Text.Json;
using SapOdooMiddleware.Models.Sap;
using Xunit;

namespace SapOdooMiddleware.Tests;

/// <summary>Odoo's sales invoice total check reads SAP's total from the invoice response as <c>doc_total</c>.</summary>
public class SapInvoiceResponseTotalTests
{
    private static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    [Fact]
    public void Doc_total_is_sent_as_doc_total()
    {
        var json = JsonSerializer.Serialize(new SapInvoiceResponse { DocEntry = 15034, DocNum = 15034, DocTotal = 3250000.0 }, Snake);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(3250000.0, doc.RootElement.GetProperty("doc_total").GetDouble());
    }

    [Fact]
    public void Doc_total_is_null_when_not_read()
    {
        var json = JsonSerializer.Serialize(new SapInvoiceResponse { DocEntry = 1 }, Snake);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("doc_total").ValueKind);
    }
}
