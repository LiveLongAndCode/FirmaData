#requires -version 5.1
<#
Stops the docker compose stack started by run.ps1. Browser tabs are left alone -- closing them
would mean killing the user's browser, so they are theirs to close.
#>

$ErrorActionPreference = 'Stop'

# docker-compose.yml lives at the repo root, one level up from web\
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Stopping containers (docker compose down)..."
# --remove-orphans clears containers left behind by an older revision of docker-compose.yml.
docker compose down --remove-orphans
if ($LASTEXITCODE -ne 0) {
    Write-Host "docker compose down failed." -ForegroundColor Red
    exit 1
}

Write-Host "Stack stopped. Remaining project containers (should be none):"
docker compose ps
