using System.Text.Json;
using SapOdooMiddleware.Models.Sap;

namespace SapOdooMiddleware.Tests;

/// <summary>
/// Tests that the <c>price</c> JSON field is accepted as an alias for <c>UnitPrice</c>
/// (Option B pricing semantics), while existing field names remain backward-compatible.
/// </summary>
public class SapSalesOrderLineRequestTests
{
    // The global snake_case naming policy is applied by the ASP.NET Core pipeline.
    // Tests here use the same options to reflect real-world deserialization behaviour.
    private static readonly JsonSerializerOptions SnakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void Deserialize_PriceField_SetsUnitPrice()
    {
        // Arrange: Odoo sends "price" (Option B semantics)
        const string json = """{"item_code":"ITEM001","quantity":2,"price":99.50}""";

        // Act
        var line = JsonSerializer.Deserialize<SapSalesOrderLineRequest>(json, SnakeCaseOptions);

        // Assert
        Assert.NotNull(line);
        Assert.Equal(99.50, line!.UnitPrice);
    }

    [Fact]
    public void Deserialize_UnitPriceField_StillWorks()
    {
        // Arrange: existing clients send "unit_price"
        const string json = """{"item_code":"ITEM001","quantity":2,"unit_price":75.00}""";

        // Act
        var line = JsonSerializer.Deserialize<SapSalesOrderLineRequest>(json, SnakeCaseOptions);

        // Assert
        Assert.NotNull(line);
        Assert.Equal(75.00, line!.UnitPrice);
    }

    [Fact]
    public void Deserialize_BothPriceAndUnitPrice_SetsUnitPrice()
    {
        // Arrange: both fields present — last one encountered wins (standard JSON behaviour).
        // "price" comes after "unit_price" in the JSON stream, so 20.00 should be the final value.
        const string json = """{"item_code":"ITEM001","quantity":1,"unit_price":10.00,"price":20.00}""";

        // Act
        var line = JsonSerializer.Deserialize<SapSalesOrderLineRequest>(json, SnakeCaseOptions);

        // Assert: the last field encountered ("price" = 20.00) takes effect
        Assert.NotNull(line);
        Assert.Equal(20.00, line!.UnitPrice);
    }

    [Fact]
    public void PriceProperty_SetDirectly_UpdatesUnitPrice()
    {
        // Arrange
        var line = new SapSalesOrderLineRequest { ItemCode = "A", Quantity = 1 };

        // Act
        line.Price = 55.00;

        // Assert
        Assert.Equal(55.00, line.UnitPrice);
    }

    [Fact]
    public void UnitPriceProperty_SetDirectly_UpdatesPrice()
    {
        // Arrange
        var line = new SapSalesOrderLineRequest { ItemCode = "A", Quantity = 1 };

        // Act
        line.UnitPrice = 123.45;

        // Assert
        Assert.Equal(123.45, line.Price);
    }

    [Fact]
    public void Deserialize_NameFieldWithSlashes_SetsNameOnRequest()
    {
        // Arrange: Odoo sends "name" containing delivery note reference with slashes
        const string json = """
        {
            "u_odoo_so_id": "SO001",
            "card_code": "C10000",
            "name": "WH/OUT/00011",
            "lines": [{"item_code":"ITEM001","quantity":1,"price":10.00}]
        }
        """;

        // Act
        var request = JsonSerializer.Deserialize<SapSalesOrderRequest>(json, SnakeCaseOptions);

        // Assert: name is deserialized correctly including slashes
        Assert.NotNull(request);
        Assert.Equal("WH/OUT/00011", request!.Name);
        Assert.Equal("WH/OUT/00011", request.ResolvedDeliveryId);
    }
}
