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

$deployments = @(
    "gateway",
    "product-service",
    "cart-service",
    "order-service",
    "payment-service",
    "user-service",
    "inventory-service",
    "shipping-service",
    "history-service"
)

foreach ($deployment in $deployments) {
    Write-Host "Waiting for deployment/$deployment..." -ForegroundColor Yellow
    kubectl rollout status deployment/$deployment -n online-store --timeout=180s
}

$gatewayUrl = minikube service gateway -n online-store --url
Write-Host "Minikube deployment completed." -ForegroundColor Green
Write-Host "Gateway URL: $gatewayUrl" -ForegroundColor Green
