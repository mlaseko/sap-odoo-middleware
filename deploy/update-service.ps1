#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Update the SAP-Odoo Middleware Windows Service with a new build.

.DESCRIPTION
    1. Stops the running service.
    2. Publishes the latest code.
    3. Restarts the service.
    appsettings.Production.json is stored in C:\SapOdoo\Config\ (outside the
    publish folder) so it is not affected by redeploys.

.PARAMETER InstallDir
    Where the service files are deployed. Default: C:\SapOdoo\Middleware Project

.PARAMETER ServiceName
    Windows Service name. Default: SapOdooMiddleware

.EXAMPLE
    .\update-service.ps1
#>
param(
    [string]$InstallDir  = "C:\SapOdoo\Middleware Project",
    [string]$ServiceName = "SapOdooMiddleware"
)

$ErrorActionPreference = "Stop"

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot   = Split-Path -Parent $scriptDir
$projectDir = Join-Path $repoRoot "src\SapOdooMiddleware"

Write-Host "`n=== SAP-Odoo Middleware — Update Service ===" -ForegroundColor Cyan

# --- Verify service exists ---
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Error "Service '$ServiceName' not found. Run .\install-service.ps1 first."
}

# --- Verify production config exists ---
$prodConfig = "C:\SapOdoo\Config\appsettings.Production.json"
if (-not (Test-Path $prodConfig)) {
    Write-Warning "Production config not found at $prodConfig. The service may use default settings."
}

# --- Stop service ---
Write-Host "`n[1/3] Stopping service..." -ForegroundColor Yellow
if ($svc.Status -eq 'Running') {
    Stop-Service $ServiceName -Force
    # Wait for the process to fully release file locks
    Start-Sleep -Seconds 3
}
Write-Host "Service stopped." -ForegroundColor Green

# --- Publish new build ---
Write-Host "`n[2/3] Publishing new build..." -ForegroundColor Yellow
dotnet publish $projectDir `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $InstallDir `
    /p:PublishSingleFile=false

if ($LASTEXITCODE -ne 0) {
    Start-Service $ServiceName
    Write-Error "dotnet publish failed. Restarted service with previous build."
}

# --- Restart service ---
Write-Host "`n[3/3] Starting service..." -ForegroundColor Yellow
Start-Service $ServiceName
Start-Sleep -Seconds 2
$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)" -ForegroundColor Green

Write-Host "`n=== Update Complete ===" -ForegroundColor Green
Write-Host "Production config: $prodConfig (untouched by publish)" -ForegroundColor Cyan
