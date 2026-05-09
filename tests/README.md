# Testing Guide

This folder contains test projects for all microservices and end-to-end gateway scenarios.

## Test projects

- `ApiVersioning.Tests`: unit tests for shared API version rewrite behavior.
- `Gateway.Tests`: gateway health smoke test.
- `OrderService.Tests`: order behavior tests (create/complete/fail/not-found paths).
- `PaymentService.Tests`: payment health + idempotency and validation behavior.
- `ProductService.Tests`: product CRUD/search/deactivate and invalid price behavior.
- `CartService.Tests`: cart mismatch/overwrite/delete behaviors.
- `UserService.Tests`: user-service health smoke test.
- `InventoryService.Tests`: inventory lifecycle, boundaries, and concurrency race behavior.
- `ShippingService.Tests`: shipment create/read behavior.
- `HistoryService.Tests`: history event create/read behavior.
- `EmailService.Tests`: email status endpoint behavior.
- `EndToEnd.Tests`: gateway-level integration, contract, resilience, rate-limit, compensation, and performance-smoke scenarios.

## Live test switch

Most service and E2E tests are designed to run against live local services and are guarded by:

- `RUN_LIVE_SERVICE_TESTS=true`

If this variable is not set to `true`, those tests return early so local/CI runs without live services still pass.

## Optional service URL overrides

Defaults point to launch settings ports. Override if needed:

- `GATEWAY_SERVICE_URL` (default `http://localhost:5152`)
- `ORDER_SERVICE_URL` (default `http://localhost:5240`)
- `PAYMENT_SERVICE_URL` (default `http://localhost:5031`)
- `PRODUCT_SERVICE_URL` (default `http://localhost:5225`)
- `CART_SERVICE_URL` (default `http://localhost:5078`)
- `USER_SERVICE_URL` (default `http://localhost:5121`)
- `INVENTORY_SERVICE_URL` (default `http://localhost:5212`)
- `SHIPPING_SERVICE_URL` (default `http://localhost:5219`)
- `HISTORY_SERVICE_URL` (default `http://localhost:5029`)
- `EMAIL_SERVICE_URL` (default `http://localhost:5164`)

## Run commands

From repo root:

### Quick helper script

```powershell
.\scripts\run-live-tests.ps1 -Suite e2e
```

Options:

- `-Suite e2e|services|all` (default `e2e`)
- optional URL overrides, e.g.:

```powershell
.\scripts\run-live-tests.ps1 -Suite all -GatewayUrl "http://localhost:5152"
```

### Run all tests

```powershell
dotnet test OnlineStore.sln
```

### Run only end-to-end tests

```powershell
dotnet test tests/EndToEnd.Tests/EndToEnd.Tests.csproj
```

### Run only service test projects (no E2E)

```powershell
dotnet test tests/Gateway.Tests/Gateway.Tests.csproj
dotnet test tests/OrderService.Tests/OrderService.Tests.csproj
dotnet test tests/PaymentService.Tests/PaymentService.Tests.csproj
dotnet test tests/ProductService.Tests/ProductService.Tests.csproj
dotnet test tests/CartService.Tests/CartService.Tests.csproj
dotnet test tests/UserService.Tests/UserService.Tests.csproj
dotnet test tests/InventoryService.Tests/InventoryService.Tests.csproj
dotnet test tests/ShippingService.Tests/ShippingService.Tests.csproj
dotnet test tests/HistoryService.Tests/HistoryService.Tests.csproj
dotnet test tests/EmailService.Tests/EmailService.Tests.csproj
dotnet test tests/ApiVersioning.Tests/ApiVersioning.Tests.csproj
```

## Typical local workflow

1. Start services (Docker Compose or local run profiles).
2. Set live-test flag:

```powershell
$env:RUN_LIVE_SERVICE_TESTS = "true"
```

3. Run:

```powershell
dotnet test tests/EndToEnd.Tests/EndToEnd.Tests.csproj
```

4. Run full suite when ready:

```powershell
dotnet test OnlineStore.sln
```
