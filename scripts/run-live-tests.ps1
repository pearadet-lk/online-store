param(
    [ValidateSet("e2e", "all", "services")]
    [string]$Suite = "e2e",
    [string]$GatewayUrl,
    [string]$OrderServiceUrl,
    [string]$PaymentServiceUrl,
    [string]$ProductServiceUrl,
    [string]$CartServiceUrl,
    [string]$UserServiceUrl,
    [string]$InventoryServiceUrl,
    [string]$ShippingServiceUrl,
    [string]$HistoryServiceUrl,
    [string]$EmailServiceUrl
)

$ErrorActionPreference = "Stop"

function Set-OptionalEnvVar {
    param(
        [string]$Name,
        [string]$Value
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        Set-Item -Path "Env:$Name" -Value $Value
        Write-Host "Set $Name=$Value"
    }
}

Set-Item -Path "Env:RUN_LIVE_SERVICE_TESTS" -Value "true"
Write-Host "Set RUN_LIVE_SERVICE_TESTS=true"

Set-OptionalEnvVar -Name "GATEWAY_SERVICE_URL" -Value $GatewayUrl
Set-OptionalEnvVar -Name "ORDER_SERVICE_URL" -Value $OrderServiceUrl
Set-OptionalEnvVar -Name "PAYMENT_SERVICE_URL" -Value $PaymentServiceUrl
Set-OptionalEnvVar -Name "PRODUCT_SERVICE_URL" -Value $ProductServiceUrl
Set-OptionalEnvVar -Name "CART_SERVICE_URL" -Value $CartServiceUrl
Set-OptionalEnvVar -Name "USER_SERVICE_URL" -Value $UserServiceUrl
Set-OptionalEnvVar -Name "INVENTORY_SERVICE_URL" -Value $InventoryServiceUrl
Set-OptionalEnvVar -Name "SHIPPING_SERVICE_URL" -Value $ShippingServiceUrl
Set-OptionalEnvVar -Name "HISTORY_SERVICE_URL" -Value $HistoryServiceUrl
Set-OptionalEnvVar -Name "EMAIL_SERVICE_URL" -Value $EmailServiceUrl

switch ($Suite) {
    "e2e" {
        dotnet test "tests/EndToEnd.Tests/EndToEnd.Tests.csproj"
    }
    "services" {
        dotnet test "tests/Gateway.Tests/Gateway.Tests.csproj"
        dotnet test "tests/OrderService.Tests/OrderService.Tests.csproj"
        dotnet test "tests/PaymentService.Tests/PaymentService.Tests.csproj"
        dotnet test "tests/ProductService.Tests/ProductService.Tests.csproj"
        dotnet test "tests/CartService.Tests/CartService.Tests.csproj"
        dotnet test "tests/UserService.Tests/UserService.Tests.csproj"
        dotnet test "tests/InventoryService.Tests/InventoryService.Tests.csproj"
        dotnet test "tests/ShippingService.Tests/ShippingService.Tests.csproj"
        dotnet test "tests/HistoryService.Tests/HistoryService.Tests.csproj"
        dotnet test "tests/EmailService.Tests/EmailService.Tests.csproj"
        dotnet test "tests/ApiVersioning.Tests/ApiVersioning.Tests.csproj"
    }
    "all" {
        dotnet test "OnlineStore.sln"
    }
}
