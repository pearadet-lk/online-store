Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Restarting Docker Desktop Kubernetes port-forwards (stop then start)..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "stop-port-forward-dockerdesktop-k8s.ps1")
& (Join-Path $PSScriptRoot "port-forward-dockerdesktop-k8s.ps1")
