# Online Store Backend Scaffold

This repository now includes a runnable ASP.NET microservice starter:

- `Gateway` (BFF/API entry)
- `ProductService` (product catalog and search)
- `CartService` (user cart; Redis-ready boundary)
- `OrderService`
- `PaymentService`
- `UserService` (auth/profile starter)
- `InventoryService`
- `ShippingService`
- `HistoryService`
- `EmailService` (Kafka consumer for order confirmation emails + delivery status)
- `Contracts` shared DTOs

## Local run (dotnet)

Run each service in separate terminals:

```powershell
dotnet run --project src/Services/OrderService
dotnet run --project src/Services/PaymentService
dotnet run --project src/Services/ProductService
dotnet run --project src/Services/CartService
dotnet run --project src/Services/UserService
dotnet run --project src/Services/InventoryService
dotnet run --project src/Services/ShippingService
dotnet run --project src/Services/HistoryService
dotnet run --project src/Services/EmailService
dotnet run --project src/Services/Gateway
```

Gateway defaults:

- Order Service: `http://localhost:5240`
- Payment Service: `http://localhost:5031`

## Checkout endpoint

Call via gateway:

```http
POST http://localhost:5152/api/checkout
Idempotency-Key: order-123-attempt-1
Content-Type: application/json

{
  "userId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "currency": "USD",
  "items": [
    {
      "productId": "11111111-1111-1111-1111-111111111111",
      "quantity": 1,
      "unitPrice": 39.99
    }
  ]
}
```

## Local run (Docker)

```powershell
docker compose up --build
```

or

```powershell
.\scripts\deploy-docker-local.ps1
```

Ports:

- Gateway: `http://localhost:8081`
- Order Service: `http://localhost:8082`
- Payment Service: `http://localhost:8083`
- Product Service: `http://localhost:8084`
- Cart Service: `http://localhost:8085`
- User Service: `http://localhost:8086`
- Inventory Service: `http://localhost:8087`
- Shipping Service: `http://localhost:8088`
- History Service: `http://localhost:8089`
- Email Service: `http://localhost:8090`
- Kafka broker: `localhost:9092`
- Jaeger UI: `http://localhost:16686`
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000` (admin/admin)
- Elasticsearch: `http://localhost:9200`
- Kibana: `http://localhost:5601`

## Observability stack

All services now expose:

- distributed traces via OpenTelemetry exported to Jaeger (through OTLP)
- HTTP metrics at `/metrics` for Prometheus
- structured Serilog logs with trace IDs, shipped to Elasticsearch
- response `X-Trace-Id` header for quick request correlation

## Kafka email flow and status

- Gateway publishes `EmailNotificationRequestedEvent` to Kafka topic `email-notifications` after successful checkout.
- `EmailService` consumes the event and sends confirmation email (SMTP if configured; mock send if SMTP is not configured).
- Delivery status is tracked per order as `Queued` -> `Processing` -> `Sent` or `Failed`.
- Check status via:
  - `GET /email/status/{orderId}`
  - `GET /email/status`

## gRPC between services

- `Gateway` now calls `InventoryService` via gRPC for inventory check/reserve/release/commit.

## Checkout saga at gateway

- Gateway now enforces auth on checkout with `Authorization: Bearer demo-jwt-<userIdN>`.
- Checkout endpoint is rate-limited (`checkout` policy) before saga execution.
- Checkout idempotency is enforced by `Idempotency-Key` + `userId` + payload hash:
  - duplicate in-flight request returns conflict
  - retry after successful completion returns cached response (prevents duplicate charges)
  - key reuse with different payload returns conflict
  - idempotency state is persisted in Redis (`Idempotency:RedisConnectionString`) with in-memory cache fallback if Redis is not configured
- Saga flow:
  - Step 1: Check inventory (gRPC)
  - Step 2: Reserve inventory (gRPC)
  - Step 3: Process payment (REST)
  - Step 4: Confirm order
- gRPC inventory calls use per-call deadline/timeout (default `3s`, configurable via `Services:InventoryGrpcTimeoutSeconds`) for faster retries and quicker circuit-breaker reaction.
- Compensation on payment failure:
  - Release inventory (gRPC)
  - Mark order as failed
- Saga state endpoint: `GET /api/checkout/sagas/{sagaId}`

## Local run (Minikube)

Prerequisites:

- Docker Desktop installed and running
- Minikube installed
- `kubectl` installed
- GNU Make installed (for `make deploy-minikube` option)

Start Minikube:

```powershell
minikube start
```

Deploy all services to Minikube:

```powershell
.\scripts\deploy-minikube.ps1
```

or using Make:

```powershell
make deploy-minikube
```

This script:

- builds all service images into Minikube Docker daemon
- applies `k8s/minikube-all-in-one.yaml`
- waits for deployments to become ready
- prints gateway URL

Manual deploy alternative:

```powershell
kubectl apply -f k8s/minikube-all-in-one.yaml
minikube service gateway -n online-store --url
```

Stop/cleanup:

```powershell
.\scripts\teardown-minikube.ps1
minikube stop
```

or using Make:

```powershell
make teardown-minikube
```

## Production-oriented patterns included

- Circuit breaker + retry on gateway downstream calls
- Idempotent payment authorization by `Idempotency-Key`
- API rate limiting policy on checkout and catalog endpoints
- CI/CD baseline with Docker build and blue/green deployment placeholder
