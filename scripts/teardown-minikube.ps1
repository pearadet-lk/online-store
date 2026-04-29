Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Stopping Minikube port-forwards..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "stop-port-forward-minikube.ps1")

Write-Host "Deleting Minikube monitoring manifests..." -ForegroundColor Cyan
kubectl delete -f k8s/minikube-monitoring.yaml --ignore-not-found=true

Write-Host "Deleting Minikube app manifests..." -ForegroundColor Cyan
kubectl delete -f k8s/minikube-all-in-one.yaml --ignore-not-found=true

Write-Host "Deleting online-store namespace from Minikube..." -ForegroundColor Cyan
kubectl delete namespace online-store --ignore-not-found=true

Write-Host "Teardown finished." -ForegroundColor Green
