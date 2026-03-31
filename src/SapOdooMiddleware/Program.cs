using System.Text.Json;
using Microsoft.OpenApi.Models;
using Serilog;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Filters;
using SapOdooMiddleware.Middleware;
using SapOdooMiddleware.Services;

var builder = WebApplication.CreateBuilder(args);

// --- External production config ---
// Load appsettings.Production.json from C:\SapOdoo\Config\ if it exists.
// This survives publish/redeploy since it lives outside the install directory.
var externalConfigDir = Path.Combine("C:", "SapOdoo", "Config");
var externalConfig = Path.Combine(externalConfigDir, "appsettings.Production.json");
if (File.Exists(externalConfig))
{
    builder.Configuration.AddJsonFile(externalConfig, optional: false, reloadOnChange: true);
}

// --- Windows Service hosting ---
// When installed as a Windows Service, UseWindowsService() sets the
// content root correctly and hooks into the SCM lifecycle (start/stop).
// When running interactively (console / dotnet run) this is a no-op.
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "SapOdooMiddleware";
});

// --- Serilog file logging ---
// Reads "Serilog" section from appsettings. Falls back to C:\SapOdoo\Logs if not configured.
builder.Host.UseSerilog((context, configuration) =>
{
    configuration.ReadFrom.Configuration(context.Configuration);
});

// --- Configuration ---
builder.Services.Configure<SapB1Settings>(builder.Configuration.GetSection(SapB1Settings.SectionName));
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection(OdooSettings.SectionName));
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));
builder.Services.Configure<WebhookQueueSettings>(builder.Configuration.GetSection(WebhookQueueSettings.SectionName));

// --- Services ---
#if WINDOWS_BUILD
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiService>();
#else
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiServiceStub>();
#endif

builder.Services.AddHttpClient<IOdooService, OdooJsonRpcService>();
builder.Services.AddHostedService<WebhookQueueProcessor>();

// --- Controllers ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

// --- Swagger ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "SAP-Odoo Middleware API",
        Version = "v1",
        Description = "Click Authorize and enter your API key to authenticate requests in Swagger UI."
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "API key via X-Api-Key header (or use Authorization: Bearer token)"
    });

    options.OperationFilter<ApiKeyOperationFilter>();
});

var app = builder.Build();

// --- Swagger UI (Development by default; override with ENABLE_SWAGGER=true) ---
bool enableSwagger = app.Environment.IsDevelopment()
    || string.Equals(Environment.GetEnvironmentVariable("ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);

if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "SAP-Odoo Middleware v1");
    });
}

// --- Startup summary ---
var webhookQueueEnabled = builder.Configuration.GetValue<bool>("WebhookQueue:Enabled");
Log.Information(
    "Middleware started — Environment={Environment}, Swagger={SwaggerEnabled}, WebhookQueue={WebhookQueueEnabled}, ExternalConfig={ExternalConfigLoaded}",
    app.Environment.EnvironmentName,
    enableSwagger,
    webhookQueueEnabled,
    File.Exists(externalConfig));

// --- Middleware ---
app.UseMiddleware<ApiKeyMiddleware>();

// --- Endpoints ---
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }