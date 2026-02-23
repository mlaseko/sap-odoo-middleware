using System.Text.Json;
using Microsoft.OpenApi.Models;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Filters;
using SapOdooMiddleware.Middleware;
using SapOdooMiddleware.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<SapB1Settings>(builder.Configuration.GetSection(SapB1Settings.SectionName));
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection(OdooSettings.SectionName));
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));

// --- Services ---
#if WINDOWS_BUILD
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiService>();
#else
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiServiceStub>();
#endif
builder.Services.AddHttpClient<IOdooService, OdooJsonRpcService>();

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
        Description = "Click **Authorize** and enter your API key to authenticate requests in Swagger UI."
    });

    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Description = "API key required for all endpoints except /health and /swagger"
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
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "SAP-Odoo Middleware v1"));
}

// --- Middleware ---
app.UseMiddleware<ApiKeyMiddleware>();

// --- Endpoints ---
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
