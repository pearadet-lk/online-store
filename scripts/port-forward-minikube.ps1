Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Checking Minikube..." -ForegroundColor Cyan
minikube status | Out-Null

$root = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $root ".port-forward"
$forwardPidJson = Join-Path $runtimeDir "minikube-port-forwards.json"
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
    @{ Service = "jaeger"; LocalPort = 16686; RemotePort = 16686; K8sService = "jaeger" },
    @{ Service = "jaeger-otlp"; LocalPort = 4317; RemotePort = 4317; K8sService = "jaeger" },
    @{ Service = "zipkin"; LocalPort = 9411; RemotePort = 9411 },
    @{ Service = "prometheus"; LocalPort = 9090; RemotePort = 9090 },
    @{ Service = "grafana"; LocalPort = 3000; RemotePort = 3000 },
    @{ Service = "elasticsearch"; LocalPort = 9200; RemotePort = 9200 },
    @{ Service = "kibana"; LocalPort = 5601; RemotePort = 5601 }
)

$records = @()
foreach ($f in $forwards) {
    $portInUse = Get-NetTCPConnection -LocalPort $f.LocalPort -State Listen -ErrorAction SilentlyContinue
    if ($null -ne $portInUse) {
        Write-Host ('Skipping {0}: localhost:{1} already in use.' -f $f.Service, $f.LocalPort) -ForegroundColor Yellow
        if ($f.Service -eq 'gateway') {
            Write-Host ('  WARNING: Gateway forward was skipped - checkout-simulator and README URLs use port {0}.' -f $f.LocalPort) -ForegroundColor Red
            Write-Host '  Fix: stop whatever is listening (or run scripts/stop-port-forward-minikube.ps1), then run this script again.' -ForegroundColor Red
        }
        continue
    }

    $k8sService = $f['K8sService']
    if ([string]::IsNullOrWhiteSpace($k8sService)) {
        $k8sService = $f['Service']
    }

    $stdout = Join-Path $logDir "$($f.Service)-stdout.log"
    $stderr = Join-Path $logDir "$($f.Service)-stderr.log"

    Write-Host "Starting port-forward svc/$k8sService ($($f.Service)) localhost:$($f.LocalPort) -> $($f.RemotePort)" -ForegroundColor Yellow
    $proc = Start-Process `
        -FilePath "kubectl" `
        -ArgumentList @("port-forward", "-n", "online-store", "svc/$k8sService", "$($f.LocalPort):$($f.RemotePort)") `
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

$records | ConvertTo-Json | Set-Content -Path $forwardPidJson -Encoding UTF8

Write-Host ''
Write-Host ('Started {0} port-forward process(es).' -f $records.Count) -ForegroundColor Green
Write-Host ('PID file: {0}' -f $forwardPidJson) -ForegroundColor Green
Write-Host ''
Write-Host 'Quick checks:' -ForegroundColor Cyan
Write-Host '  Gateway health: curl http://localhost:5152/health' -ForegroundColor Cyan
Write-Host '  Redis ping:     redis-cli -p 6379 ping' -ForegroundColor Cyan
Write-Host '  Jaeger UI:      http://localhost:16686' -ForegroundColor Cyan
Write-Host '  Jaeger OTLP:    localhost:4317 (for host apps e.g. checkout-simulator)' -ForegroundColor Cyan
Write-Host '  Zipkin UI:      http://localhost:9411' -ForegroundColor Cyan
Write-Host '  Prometheus:     http://localhost:9090' -ForegroundColor Cyan
Write-Host '  Grafana:        http://localhost:3000 (admin/admin)' -ForegroundColor Cyan
Write-Host '  Kibana:         http://localhost:5601' -ForegroundColor Cyan
Write-Host ''
Write-Host 'Final URLs on localhost (after this script; skips ports already in use):' -ForegroundColor Green
Write-Host '  Gateway (API/BFF)    http://localhost:5152' -ForegroundColor White
Write-Host '  Order service        http://localhost:5240' -ForegroundColor White
Write-Host '  Payment service      http://localhost:5031' -ForegroundColor White
Write-Host '  Product service      http://localhost:5225' -ForegroundColor White
Write-Host '  Cart service         http://localhost:5078' -ForegroundColor White
Write-Host '  User service         http://localhost:5121' -ForegroundColor White
Write-Host '  Inventory service    http://localhost:5212' -ForegroundColor White
Write-Host '  Shipping service     http://localhost:5219' -ForegroundColor White
Write-Host '  History service      http://localhost:5029' -ForegroundColor White
Write-Host '  Redis                localhost:6379' -ForegroundColor White
Write-Host '  Jaeger UI            http://localhost:16686' -ForegroundColor White
Write-Host '  Jaeger OTLP (gRPC)   localhost:4317' -ForegroundColor White
Write-Host '  Zipkin UI            http://localhost:9411' -ForegroundColor White
Write-Host '  Prometheus           http://localhost:9090' -ForegroundColor White
Write-Host '  Grafana              http://localhost:3000 (admin/admin)' -ForegroundColor White
Write-Host '  Elasticsearch        http://localhost:9200' -ForegroundColor White
Write-Host '  Kibana               http://localhost:5601' -ForegroundColor White
