Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Stopping Docker Desktop Kubernetes port-forwards..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "stop-port-forward-dockerdesktop-k8s.ps1")

Write-Host "Deleting Kubernetes monitoring manifests..." -ForegroundColor Cyan
kubectl delete -f k8s/minikube-monitoring.yaml --ignore-not-found=true

Write-Host "Deleting Kubernetes app manifests..." -ForegroundColor Cyan
kubectl delete -f k8s/minikube-all-in-one.yaml --ignore-not-found=true

Write-Host "Deleting online-store namespace..." -ForegroundColor Cyan
kubectl delete namespace online-store --ignore-not-found=true

Write-Host "Teardown finished." -ForegroundColor Green
