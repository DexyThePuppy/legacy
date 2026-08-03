# Rebuild and restart ErsatzTV on source changes (dotnet watch).
# Uses the same ports as the published build: UI 8410, streaming 8409.
#
# Prerequisites:
#   - Stop .etv-publish\ErsatzTV.exe first (same ports + singleton mutex on config folder).
#
# Usage:
#   .\scripts\dev-watch.ps1
#   .\scripts\dev-watch.ps1 -NoHotReload   # always full restart (more reliable for Blazor)

param(
    [switch]$NoHotReload
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Test-PortListening([int]$Port) {
    return $null -ne (
        Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
            Select-Object -First 1
    )
}

$portsInUse = @(8410, 8409) | Where-Object { Test-PortListening $_ }
if ($portsInUse.Count -gt 0) {
    Write-Host "Ports $($portsInUse -join ', ') are already in use." -ForegroundColor Yellow
    Write-Host "Stop the published instance before starting dev watch, e.g.:"
    Write-Host "  Get-Process ErsatzTV | Stop-Process"
    Write-Host ""
    Get-Process -Name ErsatzTV -ErrorAction SilentlyContinue |
        Format-Table Id, ProcessName, Path -AutoSize
    exit 1
}

if (Get-Process -Name ErsatzTV -ErrorAction SilentlyContinue) {
    Write-Host "ErsatzTV.exe is still running (singleton mutex blocks a second instance)." -ForegroundColor Yellow
    Write-Host "Stop it first: Get-Process ErsatzTV | Stop-Process"
    exit 1
}

Write-Host "ErsatzTV dev watch - UI http://localhost:8410, streaming :8409" -ForegroundColor Cyan
Write-Host "Ctrl+C to stop. Saves to .razor/.cs trigger rebuild/restart." -ForegroundColor DarkGray
Write-Host ""

$watchArgs = @(
    "watch", "run",
    "--project", "ErsatzTV/ErsatzTV.csproj",
    "--launch-profile", "Watch"
)

if ($NoHotReload) {
    $watchArgs += "--no-hot-reload"
}

dotnet @watchArgs
