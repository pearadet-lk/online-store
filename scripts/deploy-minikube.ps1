Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Checking Minikube..." -ForegroundColor Cyan
minikube status | Out-Null

Write-Host "Switching Docker CLI to Minikube Docker daemon..." -ForegroundColor Cyan
& minikube -p minikube docker-env --shell powershell | Invoke-Expression

$images = @(
    @{ Name = "online-store/gateway:local"; Dockerfile = "src/Services/Gateway/Dockerfile" },
    @{ Name = "online-store/product-service:local"; Dockerfile = "src/Services/ProductService/Dockerfile" },
    @{ Name = "online-store/cart-service:local"; Dockerfile = "src/Services/CartService/Dockerfile" },
    @{ Name = "online-store/order-service:local"; Dockerfile = "src/Services/OrderService/Dockerfile" },
    @{ Name = "online-store/payment-service:local"; Dockerfile = "src/Services/PaymentService/Dockerfile" },
    @{ Name = "online-store/user-service:local"; Dockerfile = "src/Services/UserService/Dockerfile" },
    @{ Name = "online-store/inventory-service:local"; Dockerfile = "src/Services/InventoryService/Dockerfile" },
    @{ Name = "online-store/shipping-service:local"; Dockerfile = "src/Services/ShippingService/Dockerfile" },
    @{ Name = "online-store/history-service:local"; Dockerfile = "src/Services/HistoryService/Dockerfile" }
)

foreach ($image in $images) {
    Write-Host "Building $($image.Name)..." -ForegroundColor Yellow
    docker build -f $image.Dockerfile -t $image.Name .
}

Write-Host "Applying Kubernetes manifests..." -ForegroundColor Cyan
kubectl apply -f k8s/minikube-all-in-one.yaml
kubectl apply -f k8s/minikube-monitoring.yaml

$deployments = @(
    "gateway",
    "product-service",
    "cart-service",
    "order-service",
    "payment-service",
    "user-service",
    "inventory-service",
    "shipping-service",
    "history-service",
    "jaeger",
    "zipkin",
    "prometheus",
    "grafana",
    "elasticsearch",
    "kibana"
)

foreach ($deployment in $deployments) {
    $rolloutTimeout = switch ($deployment) {
        "elasticsearch" { "420s" }
        "kibana" { "420s" }
        Default { "240s" }
    }
    Write-Host "Waiting for deployment/$deployment (timeout $rolloutTimeout)..." -ForegroundColor Yellow
    kubectl rollout status deployment/$deployment -n online-store --timeout=$rolloutTimeout
}

Write-Host "Starting all Minikube port-forwards..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "port-forward-minikube.ps1")

Write-Host "Minikube deployment completed." -ForegroundColor Green
Write-Host "Primary API URL: http://localhost:5152 (gateway)" -ForegroundColor Green
Write-Host "(Final localhost URLs also printed above by port-forward-minikube.ps1.)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Note: k8s/minikube-all-in-one.yaml does not deploy the React/Angular/Vue apps." -ForegroundColor Yellow
Write-Host "  - Start a UI locally (see README Frontend apps)." -ForegroundColor Yellow
Write-Host "  - Port-forwards are started automatically for gateway/services." -ForegroundColor Yellow
Write-Host "  - Frontend dev proxies can use http://localhost:5152" -ForegroundColor Yellow
Write-Host "  - Kafka and EmailService are not included in Minikube manifests yet." -ForegroundColor Yellow
Write-Host "  - Run checkout simulator: .\scripts\run-checkout-simulator-minikube.ps1 (HTTP + Jaeger; Kafka off unless you add a broker)." -ForegroundColor Yellow
Write-Host "  - Tunnel-only refresh: make restart-port-forward-minikube (if localhost:5152 refuses)." -ForegroundColor Yellow
