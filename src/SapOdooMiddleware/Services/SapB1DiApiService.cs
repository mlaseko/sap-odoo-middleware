using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SAPbobsCOM;
using SapOdooMiddleware.Configuration;
using SapOdooMiddleware.Models.Sap;
using System.Runtime.InteropServices;

namespace SapOdooMiddleware.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SapB1DiApiService : ISapB1Service, IDisposable
{
    private readonly SapB1Settings _settings;
    private readonly ILogger<SapB1DiApiService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const string SyncDirectionOdooToSap = "O2S";

    // SAP B1 DI API error codes
    private const int ErrorDbServerTypeNotSupported = -119;
    private const int ErrorSboAuthentication = -132;

    private Company? _company;
    private bool _disposed;

    public SapB1DiApiService(
        IOptions<SapB1Settings> settings,
        ILogger<SapB1DiApiService> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        _logger.LogInformation(
            "🔧 SAP DI API config loaded | Server={Server} | CompanyDB={CompanyDB} | User={User} | DbType={DbType} | LicenseServer={LicenseServer} | SLDServer={SLDServer}",
            _settings.Server,
            _settings.CompanyDb,
            _settings.UserName,
            _settings.DbServerType,
            _settings.LicenseServer,
            _settings.SLDServer);
    }

    // ================================
    // CONNECTION
    // ================================
    private void EnsureConnected() => throw new System.NotImplementedException("REPLACE_ME_SENTINEL_SPLIT_STUB");
}
