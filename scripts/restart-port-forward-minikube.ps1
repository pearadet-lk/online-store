Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Restarting Minikube port-forwards (stop then start)..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "stop-port-forward-minikube.ps1")
& (Join-Path $PSScriptRoot "port-forward-minikube.ps1")
