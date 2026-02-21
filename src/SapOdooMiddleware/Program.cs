using System.Text.Json;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Middleware;
using SapOdooMiddleware.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Services.Configure<SapB1Settings>(builder.Configuration.GetSection(SapB1Settings.SectionName));
builder.Services.Configure<OdooSettings>(builder.Configuration.GetSection(OdooSettings.SectionName));
builder.Services.Configure<ApiKeySettings>(builder.Configuration.GetSection(ApiKeySettings.SectionName));

// --- Services ---
builder.Services.AddSingleton<ISapB1Service, SapB1DiApiService>();
builder.Services.AddHttpClient<IOdooService, OdooJsonRpcService>();

// --- Controllers ---
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    });

var app = builder.Build();

// --- Middleware ---
app.UseMiddleware<ApiKeyMiddleware>();

// --- Endpoints ---
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.Run();

// Make Program class accessible for integration tests
public partial class Program { }
