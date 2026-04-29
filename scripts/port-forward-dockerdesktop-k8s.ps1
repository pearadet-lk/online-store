Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $root ".port-forward"
$pidFile = Join-Path $runtimeDir "dockerdesktop-k8s-port-forwards.json"
$logDir = Join-Path $runtimeDir "logs"

New-Item -ItemType Directory -Path $runtimeDir -Force | Out-Null
New-Item -ItemType Directory -Path $logDir -Force | Out-Null

$forwards = @(
    @{ Service = "gateway"; LocalPort = 5152; RemotePort = 8080 },
    @{ Service = "order-service"; LocalPort = 5240; RemotePort = 8080 },
    @{ Service = "payment-service"; LocalPort = 5031; RemotePort = 8080 },
    @{ Service = "product-service"; LocalPort = 5225; RemotePort = 8080 },
    @{ Service = "cart-service"; LocalPort = 5078; RemotePort = 8080 },
    @{ Service = "user-service"; LocalPort = 5121; RemotePort = 8080 },
    @{ Service = "inventory-service"; LocalPort = 5212; RemotePort = 8080 },
    @{ Service = "shipping-service"; LocalPort = 5219; RemotePort = 8080 },
    @{ Service = "history-service"; LocalPort = 5029; RemotePort = 8080 },
    @{ Service = "redis"; LocalPort = 6379; RemotePort = 6379 },
    @{ Service = "jaeger"; LocalPort = 16686; RemotePort = 16686 },
    @{ Service = "prometheus"; LocalPort = 9090; RemotePort = 9090 },
    @{ Service = "grafana"; LocalPort = 3000; RemotePort = 3000 },
    @{ Service = "elasticsearch"; LocalPort = 9200; RemotePort = 9200 },
    @{ Service = "kibana"; LocalPort = 5601; RemotePort = 5601 }
)

$records = @()
foreach ($f in $forwards) {
    $portInUse = Get-NetTCPConnection -LocalPort $f.LocalPort -State Listen -ErrorAction SilentlyContinue
    if ($null -ne $portInUse) {
        Write-Host "Skipping $($f.Service): localhost:$($f.LocalPort) already in use." -ForegroundColor Yellow
        continue
    }

    $stdout = Join-Path $logDir "$($f.Service)-stdout.log"
    $stderr = Join-Path $logDir "$($f.Service)-stderr.log"

    Write-Host "Starting port-forward svc/$($f.Service) localhost:$($f.LocalPort) -> $($f.RemotePort)" -ForegroundColor Yellow
    $proc = Start-Process `
        -FilePath "kubectl" `
        -ArgumentList @("port-forward", "-n", "online-store", "svc/$($f.Service)", "$($f.LocalPort):$($f.RemotePort)") `
        -NoNewWindow `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru

    $records += [pscustomobject]@{
        Service = $f.Service
        LocalPort = $f.LocalPort
        RemotePort = $f.RemotePort
        Pid = $proc.Id
    }
}

$records | ConvertTo-Json | Set-Content -Path $pidFile -Encoding UTF8

Write-Host ""
Write-Host "Started $($records.Count) port-forward process(es)." -ForegroundColor Green
Write-Host "PID file: $pidFile" -ForegroundColor Green
Write-Host ""
Write-Host "Quick checks:" -ForegroundColor Cyan
Write-Host "  Gateway health: curl http://localhost:5152/health" -ForegroundColor Cyan
Write-Host "  Redis ping:     redis-cli -p 6379 ping" -ForegroundColor Cyan
Write-Host "  Jaeger UI:      http://localhost:16686" -ForegroundColor Cyan
Write-Host "  Prometheus:     http://localhost:9090" -ForegroundColor Cyan
Write-Host "  Grafana:        http://localhost:3000 (admin/admin)" -ForegroundColor Cyan
Write-Host "  Kibana:         http://localhost:5601" -ForegroundColor Cyan
