#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Publish and install the SAP-Odoo Middleware as a Windows Service.

.DESCRIPTION
    1. Publishes the .NET 8 app as a self-contained Windows x64 executable.
    2. Creates the Windows Service pointing to the published exe.
    3. Configures automatic restart on failure.

.PARAMETER InstallDir
    Where to deploy the published files. Default: C:\SapOdoo\Middleware Project

.PARAMETER ServiceName
    Windows Service name. Default: SapOdooMiddleware

.PARAMETER Port
    HTTP port the API will listen on. Default: 5259

.EXAMPLE
    .\install-service.ps1
    .\install-service.ps1 -InstallDir "D:\SapOdoo" -Port 8080
#>
param(
    [string]$InstallDir  = "C:\SapOdoo\Middleware Project",
    [string]$ServiceName = "SapOdooMiddleware",
    [int]$Port           = 5259
)

$ErrorActionPreference = "Stop"

# --- Resolve paths ---
$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent $scriptDir
$projectDir = Join-Path $repoRoot "src\SapOdooMiddleware"

Write-Host "`n=== SAP-Odoo Middleware — Install as Windows Service ===" -ForegroundColor Cyan

# --- Pre-flight checks ---
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK (dotnet) not found. Install .NET 8 SDK from https://dotnet.microsoft.com/download"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Warning "Service '$ServiceName' already exists (Status: $($existing.Status))."
    Write-Warning "Use .\update-service.ps1 to update, or remove first with: sc.exe delete $ServiceName"
    exit 1
}

# --- Publish (self-contained, Windows x64) ---
Write-Host "`n[1/4] Publishing .NET app..." -ForegroundColor Yellow
dotnet publish $projectDir `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $InstallDir `
    /p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed." }
Write-Host "Published to: $InstallDir" -ForegroundColor Green

# --- Create log directory ---
$logDir = "C:\SapOdoo\Logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    Write-Host "Created log directory: $logDir" -ForegroundColor Green
}

# --- Copy production config template if not yet customized ---
$prodConfig = Join-Path $InstallDir "appsettings.Production.json"
if (-not (Test-Path $prodConfig)) {
    $templateConfig = Join-Path $projectDir "appsettings.Production.template.json"
    if (Test-Path $templateConfig) {
        Copy-Item $templateConfig $prodConfig
        Write-Host "`n[!] Copied appsettings.Production.json from template." -ForegroundColor Yellow
        Write-Host "    EDIT THIS FILE before starting the service:" -ForegroundColor Yellow
        Write-Host "    $prodConfig" -ForegroundColor White
    }
}

# --- Create Windows Service ---
Write-Host "`n[2/4] Creating Windows Service..." -ForegroundColor Yellow
$exePath = Join-Path $InstallDir "SapOdooMiddleware.exe"
sc.exe create $ServiceName `
    binPath= "`"$exePath`" --urls `"http://0.0.0.0:$Port`"" `
    start= delayed-auto `
    DisplayName= "SAP-Odoo Middleware"
if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create failed." }

# Set description
sc.exe description $ServiceName "SAP B1 <-> Odoo integration middleware API. Manages sales orders, invoices, payments, credit memos, and goods returns."

# --- Configure failure recovery (restart after 30s, 60s, 120s) ---
Write-Host "`n[3/4] Configuring failure recovery..." -ForegroundColor Yellow
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/120000

# --- Set environment to Production ---
Write-Host "`n[4/4] Setting environment variables..." -ForegroundColor Yellow
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envKey = "Environment"
# The service needs ASPNETCORE_ENVIRONMENT=Production and optionally ENABLE_SWAGGER
$envValues = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "ENABLE_SWAGGER=true"
)
# Note: sc.exe doesn't support multi-string env vars easily.
# We set it via the registry instead.
Set-ItemProperty -Path $regPath -Name $envKey -Value $envValues -Type MultiString -ErrorAction SilentlyContinue

Write-Host "`n=== Installation Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "BEFORE starting the service, you MUST:" -ForegroundColor Yellow
Write-Host "  1. Edit: $prodConfig" -ForegroundColor White
Write-Host "     - Set ApiKey:Key         (strong random string, shared with Odoo backend)" -ForegroundColor White
Write-Host "     - Set SapB1:Server        (SAP B1 SQL Server hostname)" -ForegroundColor White
Write-Host "     - Set SapB1:CompanyDb      (SAP B1 company database name)" -ForegroundColor White
Write-Host "     - Set SapB1:UserName       (SAP B1 user)" -ForegroundColor White
Write-Host "     - Set SapB1:Password       (SAP B1 password)" -ForegroundColor White
Write-Host "     - Set SapB1:LicenseServer  (license server host:port)" -ForegroundColor White
Write-Host "     - Set Odoo:BaseUrl         (e.g. https://yourcompany.odoo.com)" -ForegroundColor White
Write-Host "     - Set Odoo:Database        (Odoo database name)" -ForegroundColor White
Write-Host "     - Set Odoo:ApiKey          (Odoo API key for Bearer auth)" -ForegroundColor White
Write-Host "     - Set WebhookQueue:ConnectionString (SQL Server connection string)" -ForegroundColor White
Write-Host ""
Write-Host "  2. Then start the service:" -ForegroundColor White
Write-Host "     Start-Service $ServiceName" -ForegroundColor Cyan
Write-Host ""
Write-Host "  3. Verify health:" -ForegroundColor White
Write-Host "     Invoke-RestMethod http://localhost:$Port/health" -ForegroundColor Cyan
Write-Host ""
Write-Host "  4. Swagger UI is enabled by default (ENABLE_SWAGGER=true)." -ForegroundColor White
Write-Host "     Access at: http://localhost:$Port/swagger" -ForegroundColor Cyan
Write-Host ""
