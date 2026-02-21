using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Api;

namespace SapOdooMiddleware.Services.Sap;

public class SapServiceLayerClient : ISapServiceLayerClient
{
    private readonly HttpClient _httpClient;
    private readonly SapSettings _settings;
    private readonly ILogger<SapServiceLayerClient> _logger;
    private string? _sessionId;
    private DateTime _sessionExpiry = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SapServiceLayerClient(HttpClient httpClient, IOptions<SapSettings> settings, ILogger<SapServiceLayerClient> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(_settings.ServiceLayerUrl);
    }

    public async Task LoginAsync()
    {
        var loginPayload = new
        {
            CompanyDB = _settings.ServiceLayerCompanyDb,
            UserName = _settings.ServiceLayerUser,
            Password = _settings.ServiceLayerPassword
        };

        _logger.LogInformation("Logging into SAP Service Layer at {Url}", _settings.ServiceLayerUrl);

        var response = await _httpClient.PostAsJsonAsync("Login", loginPayload, JsonOptions);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        _sessionId = content.GetProperty("SessionId").GetString();
        _sessionExpiry = DateTime.UtcNow.AddMinutes(25);

        _logger.LogInformation("SAP Service Layer login successful, session expires at {Expiry}", _sessionExpiry);
    }

    public async Task<SalesOrderResponse> CreateSalesOrderAsync(SalesOrderRequest request)
    {
        await EnsureAuthenticatedAsync();

        var sapOrder = new
        {
            CardCode = request.CustomerCardCode,
            DocDate = request.OrderDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd"),
            DocDueDate = request.DueDate ?? DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd"),
            Comments = request.Comments ?? $"Odoo SO: {request.OdooOrderRef}",
            U_OdooRef = request.OdooOrderRef,
            DocumentLines = request.Lines.Select((line, idx) => new
            {
                line.ItemCode,
                line.Quantity,
                UnitPrice = line.Price ?? 0m,
                WarehouseCode = line.WarehouseCode ?? string.Empty,
                FreeText = line.OdooLineRef ?? string.Empty
            }).ToArray()
        };

        _logger.LogInformation("Creating sales order in SAP for customer {CardCode}, Odoo ref: {OdooRef}",
            request.CustomerCardCode, request.OdooOrderRef);

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "Orders");
        httpRequest.Headers.Add("Cookie", $"B1SESSION={_sessionId}");
        httpRequest.Content = JsonContent.Create(sapOrder, options: JsonOptions);

        var response = await _httpClient.SendAsync(httpRequest);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("SAP Service Layer error creating SO: {StatusCode} - {Body}",
                response.StatusCode, errorBody);
            throw new HttpRequestException($"SAP Service Layer error: {response.StatusCode} - {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        var soResponse = new SalesOrderResponse
        {
            DocEntry = result.GetProperty("DocEntry").GetInt32(),
            DocNum = result.GetProperty("DocNum").GetInt32(),
            OdooOrderRef = request.OdooOrderRef,
            CustomerCardCode = request.CustomerCardCode,
            DocDate = result.TryGetProperty("DocDate", out var docDate) ? docDate.GetString() ?? string.Empty : string.Empty,
            DocTotal = result.TryGetProperty("DocTotal", out var docTotal) ? docTotal.GetDecimal() : 0m,
            Status = "Open"
        };

        _logger.LogInformation("Sales order created in SAP: DocEntry={DocEntry}, DocNum={DocNum}",
            soResponse.DocEntry, soResponse.DocNum);

        return soResponse;
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            await EnsureAuthenticatedAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SAP Service Layer health check failed");
            return false;
        }
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (_sessionId == null || DateTime.UtcNow >= _sessionExpiry)
        {
            await LoginAsync();
        }
    }
}
