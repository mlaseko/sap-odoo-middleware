#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stop and remove the SAP-Odoo Middleware Windows Service.

.PARAMETER ServiceName
    Windows Service name. Default: SapOdooMiddleware

.PARAMETER RemoveFiles
    Also delete the install directory. Default: $false

.PARAMETER InstallDir
    Install directory (only used if -RemoveFiles). Default: C:\Services\SapOdooMiddleware
#>
param(
    [string]$ServiceName = "SapOdooMiddleware",
    [switch]$RemoveFiles,
    [string]$InstallDir  = "C:\Services\SapOdooMiddleware"
)

$ErrorActionPreference = "Stop"

Write-Host "`n=== SAP-Odoo Middleware — Uninstall Service ===" -ForegroundColor Cyan

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' does not exist. Nothing to do." -ForegroundColor Yellow
    exit 0
}

# Stop if running
if ($svc.Status -eq 'Running') {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force
    Start-Sleep -Seconds 2
}

# Delete service
Write-Host "Removing service..." -ForegroundColor Yellow
sc.exe delete $ServiceName
Write-Host "Service removed." -ForegroundColor Green

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Write-Host "Removing files at $InstallDir..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Files removed." -ForegroundColor Green
}

Write-Host "`n=== Uninstall Complete ===" -ForegroundColor Green
