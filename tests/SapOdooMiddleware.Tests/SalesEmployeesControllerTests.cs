using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using SapOdooMiddleware.Controllers;
using SapOdooMiddleware.Models.Api;
using SapOdooMiddleware.Models.Sap;
using SapOdooMiddleware.Services;

namespace SapOdooMiddleware.Tests;

public class SalesEmployeesControllerTests
{
    private readonly Mock<ISapB1Service> _sapServiceMock = new();
    private readonly Mock<ILogger<SalesEmployeesController>> _loggerMock = new();
    private readonly SalesEmployeesController _controller;

    public SalesEmployeesControllerTests()
    {
        _controller = new SalesEmployeesController(_sapServiceMock.Object, _loggerMock.Object);
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsOkWithSlpCode()
    {
        var request = new SapSalesEmployeeRequest
        {
            SlpName = "Mohamed Laseko",
            OdooEmployeeId = "5"
        };

        var expected = new SapSalesEmployeeResponse
        {
            SlpCode = 10,
            SlpName = "Mohamed Laseko",
            OdooEmployeeId = "5",
            Operation = "created"
        };

        _sapServiceMock
            .Setup(s => s.CreateSalesEmployeeAsync(request))
            .ReturnsAsync(expected);

        var result = await _controller.Create(request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapSalesEmployeeResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(10, response.Data!.SlpCode);
        Assert.Equal("Mohamed Laseko", response.Data.SlpName);
        Assert.Equal("5", response.Data.OdooEmployeeId);
    }

    [Fact]
    public async Task Create_ServiceThrows_Returns500()
    {
        var request = new SapSalesEmployeeRequest
        {
            SlpName = "Bad Employee",
            OdooEmployeeId = "99"
        };

        _sapServiceMock
            .Setup(s => s.CreateSalesEmployeeAsync(request))
            .ThrowsAsync(new InvalidOperationException("SAP DI API error -1: duplicate name"));

        var result = await _controller.Create(request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
    }

    [Fact]
    public async Task Update_ValidRequest_ReturnsOk()
    {
        const int slpCode = 5;
        var request = new SapSalesEmployeeRequest
        {
            SlpName = "Mohamed Laseko (Updated)",
            OdooEmployeeId = "5"
        };

        var expected = new SapSalesEmployeeResponse
        {
            SlpCode = slpCode,
            SlpName = "Mohamed Laseko (Updated)",
            OdooEmployeeId = "5",
            Operation = "updated"
        };

        _sapServiceMock
            .Setup(s => s.UpdateSalesEmployeeAsync(slpCode, request))
            .ReturnsAsync(expected);

        var result = await _controller.Update(slpCode, request);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<SapSalesEmployeeResponse>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal("updated", response.Data!.Operation);
    }

    [Fact]
    public async Task List_ReturnsAllEmployees()
    {
        var employees = new List<SapSalesEmployeeResponse>
        {
            new() { SlpCode = 1, SlpName = "Nuru Sanga", OdooEmployeeId = "1", Operation = "listed" },
            new() { SlpCode = 2, SlpName = "Danieln Ritte", OdooEmployeeId = "2", Operation = "listed" },
            new() { SlpCode = 5, SlpName = "Mohamed Laseko", OdooEmployeeId = "5", Operation = "listed" }
        };

        _sapServiceMock
            .Setup(s => s.ListSalesEmployeesAsync())
            .ReturnsAsync(employees);

        var result = await _controller.List();

        var okResult = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<List<SapSalesEmployeeResponse>>>(okResult.Value);
        Assert.True(response.Success);
        Assert.Equal(3, response.Data!.Count);
    }

    [Fact]
    public async Task Update_NotFound_Returns500()
    {
        const int slpCode = 999;
        var request = new SapSalesEmployeeRequest
        {
            SlpName = "Ghost",
            OdooEmployeeId = "999"
        };

        _sapServiceMock
            .Setup(s => s.UpdateSalesEmployeeAsync(slpCode, request))
            .ThrowsAsync(new InvalidOperationException($"Sales Employee SlpCode={slpCode} not found in SAP B1"));

        var result = await _controller.Update(slpCode, request);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, objectResult.StatusCode);
        var response = Assert.IsType<ApiResponse<SapSalesEmployeeResponse>>(objectResult.Value);
        Assert.Contains("999", response.Errors!.First());
    }
}
