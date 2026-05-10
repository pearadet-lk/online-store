Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Checkout simulator (Kubernetes host ports: Minikube or Docker Desktop K8s)" -ForegroundColor Cyan
Write-Host "  Loads tools/CheckoutSimulator/appsettings.Minikube.json via DOTNET_ENVIRONMENT=Minikube" -ForegroundColor Yellow
Write-Host "  Gateway / OTLP / microservice URLs match scripts/port-forward-minikube.ps1" -ForegroundColor Yellow
Write-Host "  Kafka:      disabled by default (bundle has no broker). Set `$env:Simulator__PublishKafka='true' if you add Kafka." -ForegroundColor Yellow
Write-Host ""
Write-Host "Tip: NodePort from the host often fails with the Docker driver; this script can start a temporary port-forward on 5152 if needed." -ForegroundColor DarkYellow
Write-Host ""

function Test-TcpPortOpen {
    param([string]$Address = '127.0.0.1', [int]$Port, [int]$TimeoutMs = 800)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($Address, $Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs, $false)) {
            return $false
        }
        $client.EndConnect($iar)
        return $client.Connected
    }
    catch {
        return $false
    }
    finally {
        try {
            $client.Close()
        }
        catch {
        }
    }
}

$gatewayForwardProcess = $null
try {
    if (-not (Test-TcpPortOpen -Port 5152)) {
        $skipAuto = $env:SKIP_AUTO_GATEWAY_FORWARD
        if ($skipAuto -eq '1' -or $skipAuto -eq 'true') {
            Write-Host "Nothing listening on localhost:5152 and SKIP_AUTO_GATEWAY_FORWARD is set." -ForegroundColor Red
            Write-Host "  Run: make port-forward-minikube   or   kubectl port-forward -n online-store svc/gateway 5152:8080" -ForegroundColor Yellow
            exit 1
        }

        Write-Host "localhost:5152 is closed - starting temporary kubectl port-forward (svc/gateway 5152->8080)..." -ForegroundColor Cyan

        $kubectlCmd = Get-Command kubectl -ErrorAction SilentlyContinue
        if (-not $kubectlCmd) {
            Write-Host "kubectl was not found on PATH. Install kubectl or forward the gateway manually." -ForegroundColor Red
            exit 1
        }

        $logDir = Join-Path $root ".port-forward/logs"
        New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        $pfOut = Join-Path $logDir "simulator-gateway-port-forward.stdout.log"
        $pfErr = Join-Path $logDir "simulator-gateway-port-forward.stderr.log"

        $gatewayForwardProcess = Start-Process -FilePath $kubectlCmd.Source `
            -ArgumentList @('port-forward', '-n', 'online-store', 'svc/gateway', '5152:8080') `
            -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $pfOut `
            -RedirectStandardError $pfErr

        $opened = $false
        for ($i = 0; $i -lt 45; $i++) {
            Start-Sleep -Milliseconds 400
            if ($gatewayForwardProcess.HasExited) {
                Write-Host "kubectl port-forward exited (code $($gatewayForwardProcess.ExitCode)). Stderr:" -ForegroundColor Red
                Get-Content $pfErr -Tail 25 -ErrorAction SilentlyContinue
                exit 1
            }
            if (Test-TcpPortOpen -Port 5152) {
                $opened = $true
                break
            }
        }

        if (-not $opened) {
            Write-Host "Port-forward did not open localhost:5152 in time. Stderr log: $pfErr" -ForegroundColor Red
            Get-Content $pfErr -Tail 25 -ErrorAction SilentlyContinue
            Get-Content $pfOut -Tail 15 -ErrorAction SilentlyContinue
            if ($gatewayForwardProcess -and -not $gatewayForwardProcess.HasExited) {
                Stop-Process -Id $gatewayForwardProcess.Id -Force -ErrorAction SilentlyContinue
            }
            exit 1
        }

        Write-Host ('Gateway on localhost:5152 (temporary kubectl PID {0}). Logs: {1} , {2}' -f $gatewayForwardProcess.Id, $pfOut, $pfErr) -ForegroundColor Green
        Write-Host ""
    }

    $env:DOTNET_ENVIRONMENT = "Minikube"
    if (-not $env:Simulator__PublishKafka) {
        $env:Simulator__PublishKafka = "false"
    }

    $checkoutProj = Join-Path $root "tools\CheckoutSimulator\CheckoutSimulator.csproj"
    dotnet run --project $checkoutProj @args
}
finally {
    if ($gatewayForwardProcess -and -not $gatewayForwardProcess.HasExited) {
        Stop-Process -Id $gatewayForwardProcess.Id -Force -ErrorAction SilentlyContinue
        Write-Host ''
        Write-Host ('Stopped temporary gateway port-forward (PID {0}).' -f $gatewayForwardProcess.Id) -ForegroundColor DarkGray
    }
}
