using System.Text.Json;
using SapOdooMiddleware.Auth;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Services.Odoo;
using SapOdooMiddleware.Services.Sap;

var builder = WebApplication.CreateBuilder(args);

// 1. Configuration binding
builder.Services.Configure<SapSettings>(builder.Configuration.GetSection("Sap"));
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection("Odoo"));
builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<SyncSettings>(builder.Configuration.GetSection("Sync"));
builder.Services.Configure<CloudflareSettings>(builder.Configuration.GetSection("Cloudflare"));

// 2. SAP Service Layer client (Scoped - one per request)
builder.Services.AddHttpClient<ISapServiceLayerClient, SapServiceLayerClient>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true
    });

// 3. Odoo JSON-RPC client
builder.Services.AddHttpClient<IOdooJsonRpcClient, OdooJsonRpcClient>();

// 4. Controllers with snake_case JSON naming
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Middleware pipeline
app.UseSwagger();
app.UseSwaggerUI();
app.UseMiddleware<CloudflareValidator>();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();
