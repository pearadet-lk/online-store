Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

param(
    [string]$GatewayBaseUrl = "http://localhost:5152",
    [string]$OrderServiceBaseUrl = "http://localhost:18082",
    [int]$TransactionCount = 30,
    [string]$LoginEmail = "demo@example.com",
    [string]$LoginPassword = "demo-password",
    [string]$Currency = "USD"
)

function New-CartItems {
    param([int]$Index)

    $productId =
        if (($Index % 2) -eq 0) {
            "11111111-1111-1111-1111-111111111111"
        }
        else {
            "22222222-2222-2222-2222-222222222222"
        }

    $unitPrice =
        if (($Index % 2) -eq 0) {
            39.99
        }
        else {
            59.99
        }

    $quantity = (($Index % 3) + 1)

    return @(
        @{
            productId = $productId
            quantity = $quantity
            unitPrice = $unitPrice
        }
    )
}

Write-Host "Logging in via Gateway: $GatewayBaseUrl" -ForegroundColor Cyan
$loginPayload = @{
    email = $LoginEmail
    password = $LoginPassword
} | ConvertTo-Json

$loginResponse = Invoke-RestMethod -Method Post -Uri "$GatewayBaseUrl/api/users/login" -ContentType "application/json" -Body $loginPayload
if (-not $loginResponse.accessToken -or -not $loginResponse.user.userId) {
    throw "Login failed. accessToken or user missing."
}

$accessToken = [string]$loginResponse.accessToken
$userId = [string]$loginResponse.user.userId
$authHeader = "Bearer $accessToken"

Write-Host "User authenticated: $userId" -ForegroundColor Green
Write-Host "Running $TransactionCount direct OrderService calls..." -ForegroundColor Cyan

$directOrderIds = New-Object System.Collections.Generic.List[string]
for ($i = 1; $i -le $TransactionCount; $i++) {
    $items = New-CartItems -Index $i
    $orderPayload = @{
        userId = $userId
        currency = $Currency
        items = $items
    } | ConvertTo-Json -Depth 5

    try {
        $directOrder = Invoke-RestMethod -Method Post -Uri "$OrderServiceBaseUrl/orders" -ContentType "application/json" -Body $orderPayload
        $directOrderIds.Add([string]$directOrder.orderId) | Out-Null
        Write-Host ("[OrderService {0}/{1}] Created order {2}" -f $i, $TransactionCount, $directOrder.orderId) -ForegroundColor Yellow
    }
    catch {
        Write-Warning ("[OrderService {0}/{1}] Failed: {2}" -f $i, $TransactionCount, $_.Exception.Message)
    }
}

Write-Host "Running $TransactionCount Gateway checkout transactions..." -ForegroundColor Cyan
$checkoutOrderIds = New-Object System.Collections.Generic.List[string]
$checkoutSuccess = 0

for ($i = 1; $i -le $TransactionCount; $i++) {
    $items = New-CartItems -Index $i
    $checkoutPayload = @{
        userId = $userId
        currency = $Currency
        items = $items
    } | ConvertTo-Json -Depth 5

    $headers = @{
        Authorization = $authHeader
        "Idempotency-Key" = ("sample-checkout-{0}-{1}" -f $i, [guid]::NewGuid().ToString("N"))
    }

    try {
        $checkout = Invoke-RestMethod -Method Post -Uri "$GatewayBaseUrl/api/checkout" -Headers $headers -ContentType "application/json" -Body $checkoutPayload
        $checkoutSuccess++
        if ($checkout.order.orderId) {
            $checkoutOrderIds.Add([string]$checkout.order.orderId) | Out-Null
        }
        Write-Host ("[Checkout {0}/{1}] OK order={2}" -f $i, $TransactionCount, $checkout.order.orderId) -ForegroundColor Green
    }
    catch {
        Write-Warning ("[Checkout {0}/{1}] Failed: {2}" -f $i, $TransactionCount, $_.Exception.Message)
    }
}

Write-Host ""
Write-Host "Verifying first 5 direct OrderService order IDs..." -ForegroundColor Cyan
$verifyCount = [Math]::Min(5, $directOrderIds.Count)
for ($i = 0; $i -lt $verifyCount; $i++) {
    $orderId = $directOrderIds[$i]
    try {
        $order = Invoke-RestMethod -Method Get -Uri "$OrderServiceBaseUrl/orders/$orderId"
        Write-Host ("[Verify {0}] orderId={1} status={2}" -f ($i + 1), $order.orderId, $order.status) -ForegroundColor Magenta
    }
    catch {
        Write-Warning ("[Verify {0}] Failed for {1}: {2}" -f ($i + 1), $orderId, $_.Exception.Message)
    }
}

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
Write-Host ("  Direct OrderService created: {0}/{1}" -f $directOrderIds.Count, $TransactionCount)
Write-Host ("  Gateway checkout success:    {0}/{1}" -f $checkoutSuccess, $TransactionCount)
Write-Host ("  Checkout orders captured:    {0}" -f $checkoutOrderIds.Count)

