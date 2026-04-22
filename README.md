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

## Local run (Minikube)

Prerequisites:

- Docker Desktop installed and running
- Minikube installed
- `kubectl` installed

Start Minikube:

```powershell
minikube start
```

Deploy all services to Minikube:

```powershell
.\scripts\deploy-minikube.ps1
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

## Production-oriented patterns included

- Circuit breaker + retry on gateway downstream calls
- Idempotent payment authorization by `Idempotency-Key`
- API rate limiting policy on checkout and catalog endpoints
- CI/CD baseline with Docker build and blue/green deployment placeholder
