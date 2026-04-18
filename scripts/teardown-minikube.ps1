Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Deleting online-store namespace from Minikube..." -ForegroundColor Cyan
kubectl delete namespace online-store --ignore-not-found=true

Write-Host "Teardown finished." -ForegroundColor Green
