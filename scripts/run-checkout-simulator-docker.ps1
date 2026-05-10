Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Checkout simulator (Docker Compose defaults)" -ForegroundColor Cyan
Write-Host "  Uses appsettings.json: Gateway http://localhost:8081/, OTLP http://localhost:4317, Kafka publish on unless Simulator__PublishKafka overrides." -ForegroundColor Yellow
Write-Host "  Start stack first: make deploy-docker-local (or docker compose up --build -d from repo root)." -ForegroundColor Yellow
Write-Host ""

Remove-Item Env:\DOTNET_ENVIRONMENT -ErrorAction SilentlyContinue
if (-not $env:Simulator__PublishKafka) {
    $env:Simulator__PublishKafka = "true"
}

dotnet run --project "$root/tools/CheckoutSimulator/CheckoutSimulator.csproj" @args
