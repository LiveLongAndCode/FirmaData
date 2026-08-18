#requires -version 5.1
<#
Starts the docker compose stack and opens the four service URLs from the README once the API is healthy.
#>

$ErrorActionPreference = 'Stop'

# docker-compose.yml lives at the repo root, one level up from web\
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Starting containers (docker compose up --build)..."
docker compose up --build -d
if ($LASTEXITCODE -ne 0) {
    Write-Host "docker compose failed to start. Aborting." -ForegroundColor Red
    exit 1
}

Write-Host "Waiting for the API to report healthy..."
$healthUrl = 'http://localhost:8080/health/ready'
$ready = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {}
    Start-Sleep -Seconds 2
}
if (-not $ready) {
    Write-Host "Timed out waiting for API health check; opening tabs anyway." -ForegroundColor Yellow
}

Write-Host "Opening browser tabs..."
Start-Sleep -Seconds 1
Start-Process 'http://localhost:8090/'
Start-Process 'http://localhost:8080/swagger'
Start-Process 'http://localhost:9090/'
Start-Sleep -Seconds 3
Start-Process 'http://localhost:3000/d/firmadata/firmadata?orgId=1&refresh=15s'
