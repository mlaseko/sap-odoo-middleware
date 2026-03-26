using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class CustomersControllerTests
{
    private readonly Mock<ISapB1Service> _sapServiceMock = new();
    private readonly Mock<ILogger<CustomersController>> _loggerMock = new();
    private readonly CustomersController _controller;

    public CustomersControllerTests()
    {
        _controller = new CustomersController(_sapServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsOkWithSapResponse()
    {
        // Arrange
        var request = new SapCustomerRequest
        {
            CardName = "Molas Lubes Ltd",
            Phone1 = "+255743378372",
            Phone2 = "+255743378373",
            Email = "info@molaslubes.com",
            OdooCustomerId = "42",
            GroupCode = 100,
            BillTo = new SapCustomerAddressRequest
            {
                Street = "Plot 123 Industrial Area",
                City = "Dar es Salaam",
                Country = "TZ"
            }
        };

        var expected = new SapCustomerResponse
        {
            CardCode = "C00042",
            CardName = "Molas Lubes Ltd",
            OdooCustomerId = "42",
            Operation = "created"
        };

        _sapServiceMock
            .Setup(s => s.CreateCustomerAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapCustomerResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("C00042", response.Data!.CardCode);
        Assert.Equal("Molas Lubes Ltd", response.Data.CardName);
        Assert.Equal("42", response.Data.OdooCustomerId);
        Assert.Equal("created", response.Data.Operation);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500WithError()
    {
        // Arrange
        var request = new SapCustomerRequest
        {
            CardName = "Bad Customer",
            Phone1 = "+255000000000",
            OdooCustomerId = "99"
        };

        _sapServiceMock
            .Setup(s => s.CreateCustomerAsync(request))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error -1: CardName is mandatory"));

        // Act
        var result = await _controller.Create(request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapCustomerResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains("SAP DI API error", response.Errors!.First());
    }

    [Fact]
    public async Task Update_ValidRequest_ReturnsOkWithSapResponse()
    {
        // Arrange
        const string cardCode = "C00042";
        var request = new SapCustomerRequest
        {
            CardName = "Molas Lubes Ltd (Updated)",
            Phone1 = "+255743378372",
            Email = "updated@molaslubes.com",
            OdooCustomerId = "42",
            GroupCode = 100
        };

        var expected = new SapCustomerResponse
        {
            CardCode = cardCode,
            CardName = "Molas Lubes Ltd (Updated)",
            OdooCustomerId = "42",
            Operation = "updated"
        };

        _sapServiceMock
            .Setup(s => s.UpdateCustomerAsync(cardCode, request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Update(cardCode, request);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapCustomerResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(cardCode, response.Data!.CardCode);
        Assert.Equal("updated", response.Data.Operation);
    }

    [Fact]
    public async Task Update_CustomerNotFound_Returns500WithError()
    {
        // Arrange
        const string cardCode = "C99999";
        var request = new SapCustomerRequest
        {
            CardName = "Ghost Customer",
            Phone1 = "+255000000000",
            OdooCustomerId = "999"
        };

        _sapServiceMock
            .Setup(s => s.UpdateCustomerAsync(cardCode, request))
            .ThrowsAsync(new InvalidOperationException($"Customer '{cardCode}' not found in SAP B1"));

        // Act
        var result = await _controller.Update(cardCode, request);

        // Assert
        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapCustomerResponse>>(objectResult.Value);
        Assert.False(response.Success);
        Assert.Contains(cardCode, response.Errors!.First());
    }

    [Fact]
    public async Task Create_WithAddresses_PassesThroughToService()
    {
        // Arrange
        var request = new SapCustomerRequest
        {
            CardName = "Address Test Customer",
            Phone1 = "+255111111111",
            OdooCustomerId = "55",
            BillTo = new SapCustomerAddressRequest
            {
                Street = "123 Bill Street",
                City = "Dar es Salaam",
                Country = "TZ",
                ZipCode = "11000"
            },
            ShipTo = new SapCustomerAddressRequest
            {
                Street = "456 Ship Road",
                City = "Arusha",
                Country = "TZ"
            }
        };

        var expected = new SapCustomerResponse
        {
            CardCode = "C00055",
            CardName = "Address Test Customer",
            OdooCustomerId = "55",
            Operation = "created"
        };

        _sapServiceMock
            .Setup(s => s.CreateCustomerAsync(request))
            .ReturnsAsync(expected);

        // Act
        var result = await _controller.Create(request);

        // Assert
        Assert.IsType<OkObjectResult>(result);
        _sapServiceMock.Verify(s => s.CreateCustomerAsync(request), Times.Once);
    }
}
