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

## Frontend apps

This repository now contains three frontend projects:

- React (existing): `src/frontend/react`
- Angular (new): `src/frontend/angular` (Angular `20.x`, which is latest major minus one)
- Vue (new): `src/frontend/vue` (Vue `3.x`)

Run the Angular app:

```powershell
cd src/frontend/angular
npm install
npm start
```

Default Angular dev URL: `http://localhost:4200`

Run the Vue app:

```powershell
cd src/frontend/vue
npm install
npm run dev
```

Default Vue dev URL: `http://localhost:5173`

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

## Sample transaction runners

You now have two ready scripts to generate sample traffic:

- PowerShell flow runner: `scripts/sample-order-and-checkout.ps1`
- k6 load runner: `scripts/k6-order-checkout.js`

### 1) PowerShell sample runner (30 direct order + 30 checkout)

Default run (uses `http://localhost:5152` for gateway and `http://localhost:18082` for OrderService):

```powershell
.\scripts\sample-order-and-checkout.ps1
```

With custom URLs / transaction count:

```powershell
.\scripts\sample-order-and-checkout.ps1 `
  -GatewayBaseUrl "http://localhost:8081" `
  -OrderServiceBaseUrl "http://localhost:8082" `
  -TransactionCount 30
```

### 2) k6 load runner (default 30 iterations)

Default run:

```powershell
k6 run .\scripts\k6-order-checkout.js
```

With explicit environment variables (Docker Compose example):

```powershell
$env:GATEWAY_URL = "http://localhost:8081"
$env:ORDER_URL = "http://localhost:8082"
$env:VUS = "5"
$env:ITERATIONS = "30"
k6 run .\scripts\k6-order-checkout.js
```

With Minikube port-forwards:

```powershell
$env:GATEWAY_URL = "http://localhost:5152"
$env:ORDER_URL = "http://localhost:18082"
k6 run .\scripts\k6-order-checkout.js
```

## Local run (Docker)

```powershell
docker compose up --build
```

or

```powershell
.\scripts\deploy-docker-local.ps1
```

Final gateway URL after local Docker deploy:

- `http://localhost:8081`

### Accessing services (Docker Compose)

Everything below is on your host (`localhost`) because `docker-compose.yml` publishes ports.


| Component           | URL / host:port                                                                    |
| ------------------- | ---------------------------------------------------------------------------------- |
| Gateway (API entry) | `http://localhost:8081`                                                            |
| Order Service       | `http://localhost:8082`                                                            |
| Payment Service     | `http://localhost:8083`                                                            |
| Product Service     | `http://localhost:8084`                                                            |
| Cart Service        | `http://localhost:8085`                                                            |
| User Service        | `http://localhost:8086`                                                            |
| Inventory Service   | `http://localhost:8087`                                                            |
| Shipping Service    | `http://localhost:8088`                                                            |
| History Service     | `http://localhost:8089`                                                            |
| Email Service       | `http://localhost:8090`                                                            |
| PostgreSQL          | `localhost:5432` (user/password/db: `onlinestore` / `onlinestore` / `onlinestore`) |
| Redis               | `localhost:6379`                                                                   |
| Kafka broker        | `localhost:9092`                                                                   |
| Jaeger UI           | `http://localhost:16686`                                                           |
| Prometheus          | `http://localhost:9090`                                                            |
| Grafana             | `http://localhost:3000` (admin/admin)                                              |
| Elasticsearch       | `http://localhost:9200`                                                            |
| Kibana              | `http://localhost:5601`                                                            |


**Frontends (Docker):** the UI apps are not started by Compose. Run them locally (see [Frontend apps](#frontend-apps)). Point dev proxies at the gateway URL you use (`http://localhost:8081` when the stack runs in Docker, or `http://localhost:5152` when you run the gateway with `dotnet run`).

## Kubernetes quick start (choose one)

Minikube and Docker Desktop Kubernetes are separate clusters. Use one at a time.

Docker Desktop Kubernetes:

```powershell
kubectl config use-context docker-desktop
.\scripts\deploy-dockerdesktop-k8s.ps1
```

Minikube:

```powershell
minikube start
.\scripts\deploy-minikube.ps1
```

Verify:

```powershell
curl http://localhost:5152/health
```

Stop:

```powershell
.\scripts\teardown-dockerdesktop-k8s.ps1   # if using docker-desktop
.\scripts\teardown-minikube.ps1            # if using minikube
```

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

> Minikube and Docker Desktop Kubernetes are separate clusters.
> Enabling Kubernetes in Docker Desktop does not run Minikube inside Docker Desktop.
> Use one context at a time (`minikube` or `docker-desktop`).

Prerequisites:

- Docker Desktop installed and running
- Minikube installed
- `kubectl` installed
- GNU Make installed (for `make deploy-minikube` option)

Start Minikube:

```powershell
minikube start
```

Deploy all services (app + monitoring stack) to Minikube:

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
- applies `k8s/minikube-monitoring.yaml` (Jaeger, Prometheus, Grafana, Elasticsearch, Kibana)
- waits for deployments to become ready
- prints gateway URL

Manual deploy alternative:

```powershell
kubectl apply -f k8s/minikube-all-in-one.yaml
kubectl apply -f k8s/minikube-monitoring.yaml
minikube service gateway -n online-store --url
```

### Accessing services (Minikube)

`k8s/minikube-all-in-one.yaml` + `k8s/minikube-monitoring.yaml` deploy backend workloads and monitoring tools. It still does **not** deploy frontend containers (React/Angular/Vue), so there is **no in-cluster frontend URL** until you add a frontend Deployment (for example nginx serving static build).


| Component               | How to reach it from your PC                                                                                                                                                |
| ----------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Gateway**             | **NodePort** `30081` on the Minikube node: `http://<minikube-ip>:30081` (`minikube ip`). Or open a tunnel and print a URL: `minikube service gateway -n online-store --url` |
| **Other microservices** | **ClusterIP only** (no NodePort). Call them through the gateway, or use port-forward (examples below).                                                                      |
| **Redis**               | ClusterIP only; use port-forward if you need it from the host.                                                                                                              |
| **Jaeger / Prometheus / Grafana / ELK** | ClusterIP only; use port-forward for local browser access. |


**Why the browser cannot open “the frontend” after Minikube deploy:** nothing in that manifest serves the UI. Start a frontend dev server on your machine and point it at the gateway (next paragraph).

**Recommended: gateway port-forward so local dev proxies keep working**

The Angular proxy and Vite proxies default to `http://localhost:5152`. After Minikube is up, forward the in-cluster gateway to that port (leave the terminal open):

```powershell
kubectl port-forward -n online-store svc/gateway 5152:8080
```

Final gateway URL after port-forward:

- `http://localhost:5152`

Then start Angular / Vue / React as usual; `/api` and `/health` requests go to Minikube’s gateway.

### Final URLs after port-forward (Minikube)

Use these port-forwards (leave terminals open):

```powershell
kubectl port-forward -n online-store svc/gateway 5152:8080
kubectl port-forward -n online-store svc/product-service 18084:8080
kubectl port-forward -n online-store svc/cart-service 18085:8080
kubectl port-forward -n online-store svc/order-service 18082:8080
kubectl port-forward -n online-store svc/payment-service 18083:8080
kubectl port-forward -n online-store svc/user-service 18086:8080
kubectl port-forward -n online-store svc/inventory-service 18087:8080
kubectl port-forward -n online-store svc/shipping-service 18088:8080
kubectl port-forward -n online-store svc/history-service 18089:8080
kubectl port-forward -n online-store svc/jaeger 16686:16686
kubectl port-forward -n online-store svc/prometheus 9090:9090
kubectl port-forward -n online-store svc/grafana 3000:3000
kubectl port-forward -n online-store svc/elasticsearch 9200:9200
kubectl port-forward -n online-store svc/kibana 5601:5601
```

Access URLs from your PC:


| Component                           | Final URL                                          |
| ----------------------------------- | -------------------------------------------------- |
| Gateway (BFF/API)                   | `http://localhost:5152`                            |
| Product Service                     | `http://localhost:18084`                           |
| Cart Service                        | `http://localhost:18085`                           |
| Order Service                       | `http://localhost:18082`                           |
| Payment Service                     | `http://localhost:18083`                           |
| User Service                        | `http://localhost:18086`                           |
| Inventory Service                   | `http://localhost:18087`                           |
| Shipping Service                    | `http://localhost:18088`                           |
| History Service                     | `http://localhost:18089`                           |
| Jaeger UI                           | `http://localhost:16686`                           |
| Prometheus                          | `http://localhost:9090`                            |
| Grafana                             | `http://localhost:3000` (`admin` / `admin`)       |
| Elasticsearch                       | `http://localhost:9200`                            |
| Kibana                              | `http://localhost:5601`                            |
| Angular frontend (local dev server) | `http://localhost:4200`                            |
| React frontend (local dev server)   | `http://localhost:5173`                            |
| Vue frontend (local dev server)     | `http://localhost:5174` (run separately from React) |


**Alternative:** set the Vite gateway target when using React or Vue:

```powershell
$env:VITE_GATEWAY_TARGET = (minikube service gateway -n online-store --url).Trim()
cd src/frontend/react   # or vue
npm run dev
```

For Angular, either keep the port-forward to `5152:8080` above, or change `src/frontend/angular/proxy.conf.json` to the URL printed by `minikube service gateway -n online-store --url`.

### Not included in Minikube deploy (current state)

- Frontend container deployments (React/Angular/Vue) are not in Kubernetes yet.
- Kafka broker + EmailService deployment are not in `k8s/minikube-all-in-one.yaml` yet.
- Production manifests under `k8s/production/` are separate and not used by `deploy-minikube.ps1`.

**Optional: hit individual services from the host** (replace local ports as you like):

```powershell
kubectl port-forward -n online-store svc/product-service 18084:8080
kubectl port-forward -n online-store svc/cart-service 18085:8080
kubectl port-forward -n online-store svc/order-service 18082:8080
kubectl port-forward -n online-store svc/payment-service 18083:8080
kubectl port-forward -n online-store svc/user-service 18086:8080
kubectl port-forward -n online-store svc/inventory-service 18087:8080
kubectl port-forward -n online-store svc/shipping-service 18088:8080
kubectl port-forward -n online-store svc/history-service 18089:8080
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

## Local run (Docker Desktop Kubernetes)

Prerequisites:

- Docker Desktop installed and running
- Kubernetes enabled in Docker Desktop
- `kubectl` installed
- current kube context set to `docker-desktop` (or let script warn you)

Switch context:

```powershell
kubectl config use-context docker-desktop
```

Deploy all services (app + monitoring stack):

```powershell
.\scripts\deploy-dockerdesktop-k8s.ps1
```

or using Make:

```powershell
make deploy-dockerdesktop-k8s
```

This script:

- builds all service images into your normal Docker daemon
- applies `k8s/minikube-all-in-one.yaml`
- applies `k8s/minikube-monitoring.yaml` (Jaeger, Prometheus, Grafana, Elasticsearch, Kibana)
- waits for deployments to become ready
- starts port-forwards automatically

Final gateway URL after deploy:

- `http://localhost:5152`

Stop/cleanup:

```powershell
.\scripts\teardown-dockerdesktop-k8s.ps1
```

or using Make:

```powershell
make teardown-dockerdesktop-k8s
```

## Production-oriented patterns included

- Circuit breaker + retry on gateway downstream calls
- Idempotent payment authorization by `Idempotency-Key`
- API rate limiting policy on checkout and catalog endpoints
- CI/CD baseline with Docker build and blue/green deployment placeholder

