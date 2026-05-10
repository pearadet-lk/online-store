Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Starting local Docker Compose environment..." -ForegroundColor Cyan
docker compose up --build -d

Write-Host "Services are running. Gateway: http://localhost:8081" -ForegroundColor Green
Write-Host "Observability: Jaeger http://localhost:16686 | Zipkin http://localhost:9411 | Grafana http://localhost:3000" -ForegroundColor Green
