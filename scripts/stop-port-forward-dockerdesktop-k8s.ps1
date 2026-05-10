Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$runtimeDir = Join-Path $root ".port-forward"
$forwardPidJson = Join-Path $runtimeDir "dockerdesktop-k8s-port-forwards.json"

if (-not (Test-Path $forwardPidJson)) {
    Write-Host "No PID file found at $forwardPidJson. Nothing to stop." -ForegroundColor Yellow
    exit 0
}

$entries = Get-Content -Path $forwardPidJson -Raw | ConvertFrom-Json
if ($entries -isnot [System.Array]) {
    $entries = @($entries)
}

foreach ($entry in $entries) {
    $proc = Get-Process -Id $entry.Pid -ErrorAction SilentlyContinue
    if ($null -eq $proc) {
        Write-Host "PID $($entry.Pid) for $($entry.Service) is not running." -ForegroundColor Yellow
        continue
    }

    Stop-Process -Id $entry.Pid -Force
    Write-Host "Stopped $($entry.Service) (PID $($entry.Pid))." -ForegroundColor Green
}

Remove-Item -Path $forwardPidJson -Force
Write-Host "All tracked Docker Desktop Kubernetes port-forwards are stopped." -ForegroundColor Green
