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

## Checkout simulator (Jaeger + Kafka)

The **`tools/CheckoutSimulator`** console app logs in as the demo user, runs a realistic checkout through the gateway (cart, inventory, order, payment, shipping, history), exports **OTLP** traces (so you see the call chain in **Jaeger**), and publishes an **email notification** message to **Kafka** for `email-service` to process. See [tools/CheckoutSimulator/README.md](tools/CheckoutSimulator/README.md).

```powershell
docker compose up --build -d
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
# or: make run-checkout-simulator-docker   (same defaults: gateway 8081)
```

Default simulator settings target **Docker Compose** (gateway **`localhost:8081`**, OTLP **`4317`**, Kafka on). For **Minikube**, gateway traffic is **`localhost:5152`** after port-forward — use `.\scripts\run-checkout-simulator-minikube.ps1` or set `DOTNET_ENVIRONMENT=Minikube` so `appsettings.Minikube.json` applies. To flip stacks **without editing files**, set **`Gateway__BaseUrl`** (and for Minikube typically **`Simulator__PublishKafka=false`**). Details: [tools/CheckoutSimulator/README.md](tools/CheckoutSimulator/README.md#docker-compose-vs-minikube).

**Minikube:** after `.\scripts\deploy-minikube.ps1` (or `make deploy-minikube`), port-forwards include gateway **5152**, PostgreSQL **55432** (for DBeaver), and Jaeger OTLP **4317**. The Minikube manifest set does not include Kafka, so the simulator disables Kafka publish unless you override `Simulator__PublishKafka`.

```powershell
.\scripts\run-checkout-simulator-minikube.ps1
# or: make run-checkout-simulator-minikube
```

If **`5152` refuses connections**, stale tunnels are likely — **`make stop-port-forward-minikube`** then **`make port-forward-minikube`**, or **`make restart-port-forward-minikube`**. See [Minikube checkout checklist](#minikube-checkout-checklist).

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

Gateway default URL:

- `http://localhost:5152`

## YARP gateway architecture

`Gateway` is now a YARP-based boundary gateway (proxy/security/observability layer), not a business-logic orchestrator.

Gateway responsibilities:

- route proxying to downstream services
- JWT validation once at gateway
- forwarding user claims in internal headers
- correlation ID and request logging
- rate limiting
- health/docs/metrics endpoints

Gateway does **not** implement order/payment/product business workflows.

### How routing works

Gateway routes incoming `/api/*` calls to downstream services using YARP config in:

- `src/Services/Gateway/ReverseProxy/yarp.json`

Examples:

- `/api/users/*` -> `UserService`
- `/api/products/*` -> `ProductService`
- `/api/orders/*` -> `OrderService`
- `/api/payments/*` -> `PaymentService`
- `/api/carts/*` -> `CartService`
- `/api/inventory/*` -> `InventoryService`
- `/api/shipments/*` -> `ShippingService`
- `/api/history/*` -> `HistoryService`
- `/api/email/*` -> `EmailService`

### Authentication model

- Public endpoints:
  - `/api/users/login`
  - `/api/users/register`
  - `/api/users/refresh`
- Other `/api/*` routes require valid `Authorization: Bearer <jwt>` at gateway.
- Gateway forwards authenticated identity to downstream services via:
  - `X-Authenticated-UserId`
  - `X-Authenticated-Email`
  - `X-Authenticated-Name`

### Gateway-specific endpoints

- `GET /health` -> gateway health
- `GET /api/docs` -> aggregated list of downstream Swagger URLs
- `GET /metrics` -> Prometheus metrics

## API versioning

All backend services now support URL version prefix `v1`.

- Existing routes continue to work (for backward compatibility).
- Versioned routes are available by prefixing with `/api/v1`.
- Response header `api-supported-versions: v1` is returned by each service.

Examples:

- Gateway health: `http://localhost:5152/api/v1/health`
- Gateway products: `http://localhost:5152/api/v1/products`
- Gateway checkout: `http://localhost:5152/api/v1/checkout`
- Direct Order service (port-forward): `http://localhost:18082/api/v1/orders`

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

- distributed traces via OpenTelemetry exported to Jaeger (OTLP) and Zipkin (when `Observability:ZipkinEndpoint` is set; enabled in Docker Compose and the bundled Kubernetes manifests)
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

## JWT refresh token storage

Refresh tokens are persisted in PostgreSQL schema `user_service` (table `refresh_tokens`) with:

- `token_hash` (SHA-256 hash of token, never stores raw token)
- `user_id`
- `expires_at`
- `revoked_at`
- `replaced_by_token_hash` (for token rotation chain)

Frontend apps (React, Angular, Vue) now uniformly handle backend `401` by calling `/api/users/refresh`, updating local session tokens, and retrying the original request once.

## gRPC between services

- `Gateway` now calls `InventoryService` via gRPC for inventory check/reserve/release/commit.

## YARP verification checklist

After deploy, verify:

```powershell
curl http://localhost:5152/health
curl http://localhost:5152/api/docs
curl -i http://localhost:5152/api/products
```

Expected:

- `/health` returns `200`
- `/api/docs` returns `200`
- `/api/products` without JWT returns `401`

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
- waits for deployments to become ready (`postgres` and `redis` first, then the rest)
- runs **`scripts/port-forward-minikube.ps1`** (gateway **5152**, direct service ports, **PostgreSQL `localhost:55432`**, Redis, observability)
- prints gateway URL and a PostgreSQL / DBeaver hint

Manual deploy alternative:

```powershell
kubectl apply -f k8s/minikube-all-in-one.yaml
kubectl apply -f k8s/minikube-monitoring.yaml
minikube service gateway -n online-store --url
```

### Minikube checkout checklist

Gateway (`5152`), checkout simulator, and Jaeger OTLP (`4317`):

Stale **`kubectl port-forward`** sessions cause **connection refused** on `localhost:5152` or **`Skipping … already in use`** when scripts skip gateway.

1. **`make stop-port-forward-minikube`** before reuse or redeploy (clears forwards tracked under `.port-forward/`).
2. **`make deploy-minikube`** (builds, applies manifests, waits for rollouts, starts forwards) — or after manual **`kubectl apply`**, run **`make port-forward-minikube`**.
3. **`curl http://localhost:5152/health`** should return **`200`**.
4. **`make run-checkout-simulator-minikube`** — drives checkout via gateway + OTLP to Jaeger (Kafka stays off unless you configure it). **`scripts/run-checkout-simulator-minikube.ps1`** starts a **temporary `kubectl port-forward`** on **5152** when nothing is listening there (common with the Docker driver). Set **`SKIP_AUTO_GATEWAY_FORWARD=1`** to disable that behavior.

**502 on `/api/...` while pods are Running:** If gateway logs show **`Proxying to http://localhost:5121/...`** inside the cluster, **`ReverseProxy/yarp.json`** overwrote **`ReverseProxy__Clusters__...`** env vars from **`gateway-config`**. Rebuild **`online-store/gateway:local`** from current **`Gateway/Program.cs`** (environment variables are reapplied after **`yarp.json`** so Minikube destinations stay **`http://user-service:8080/`**, etc.).

**Tunnel-only refresh:** **`make restart-port-forward-minikube`** (runs **`scripts/stop-port-forward-minikube.ps1`** then **`scripts/port-forward-minikube.ps1`**) when the cluster is healthy but forwards exited.

Deploy waits: **Elasticsearch** and **Kibana** rollouts allow **420s**; other deployments **240s** (see **`scripts/deploy-minikube.ps1`**).

### Accessing services (Minikube)

`k8s/minikube-all-in-one.yaml` + `k8s/minikube-monitoring.yaml` deploy backend workloads and monitoring tools. It still does **not** deploy frontend containers (React/Angular/Vue), so there is **no in-cluster frontend URL** until you add a frontend Deployment (for example nginx serving static build).


| Component               | How to reach it from your PC                                                                                                                                                |
| ----------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Gateway**             | **NodePort** `30081` on the Minikube node: `http://<minikube-ip>:30081` (`minikube ip`). Or open a tunnel and print a URL: `minikube service gateway -n online-store --url` |
| **Other microservices** | **ClusterIP only** (no NodePort). Call them through the gateway, or use port-forward (examples below).                                                                      |
| **Redis**               | ClusterIP only; use port-forward if you need it from the host.                                                                                                              |
| **PostgreSQL**          | ClusterIP only; **`make port-forward-minikube`** (or **`make port-forward-dockerdesktop-k8s`**) forwards **`localhost:55432`** to the in-cluster DB (see [PostgreSQL from the host (DBeaver)](#postgresql-from-the-host-dbeaver)). |
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

**`.\scripts\deploy-minikube.ps1`** and **`make deploy-minikube`** finish by running **`scripts/port-forward-minikube.ps1`**, which prints the same localhost URLs to your terminal. **`make port-forward-minikube`** uses the same port map.

These are the URLs when every forward starts successfully (the script skips any local port that is already in use):

| Component | Final URL |
| --------- | --------- |
| Gateway (BFF/API) | `http://localhost:5152` |
| Order service | `http://localhost:5240` |
| Payment service | `http://localhost:5031` |
| Product service | `http://localhost:5225` |
| Cart service | `http://localhost:5078` |
| User service | `http://localhost:5121` |
| Inventory service | `http://localhost:5212` |
| Shipping service | `http://localhost:5219` |
| History service | `http://localhost:5029` |
| PostgreSQL | `localhost:55432` (maps to in-cluster `5432`; use this port in DBeaver so it does not clash with Docker Compose on `5432`) |
| Redis | `localhost:6379` |
| Jaeger UI | `http://localhost:16686` |
| Jaeger OTLP (gRPC; host tools / checkout-simulator) | `localhost:4317` |
| Zipkin UI | `http://localhost:9411` |
| Prometheus | `http://localhost:9090` |
| Grafana | `http://localhost:3000` (`admin` / `admin`) |
| Elasticsearch | `http://localhost:9200` |
| Kibana | `http://localhost:5601` |

Local frontends (not deployed to Minikube): Angular `http://localhost:4200`, React `http://localhost:5173`, Vue `http://localhost:5174`.

Equivalent **`kubectl port-forward`** commands (one terminal each, or use the script):

```powershell
kubectl port-forward -n online-store svc/gateway 5152:8080
kubectl port-forward -n online-store svc/order-service 5240:8080
kubectl port-forward -n online-store svc/payment-service 5031:8080
kubectl port-forward -n online-store svc/product-service 5225:8080
kubectl port-forward -n online-store svc/cart-service 5078:8080
kubectl port-forward -n online-store svc/user-service 5121:8080
kubectl port-forward -n online-store svc/inventory-service 5212:8080
kubectl port-forward -n online-store svc/shipping-service 5219:8080
kubectl port-forward -n online-store svc/history-service 5029:8080
kubectl port-forward -n online-store svc/postgres 55432:5432
kubectl port-forward -n online-store svc/redis 6379:6379
kubectl port-forward -n online-store svc/jaeger 16686:16686
kubectl port-forward -n online-store svc/jaeger 4317:4317
kubectl port-forward -n online-store svc/zipkin 9411:9411
kubectl port-forward -n online-store svc/prometheus 9090:9090
kubectl port-forward -n online-store svc/grafana 3000:3000
kubectl port-forward -n online-store svc/elasticsearch 9200:9200
kubectl port-forward -n online-store svc/kibana 5601:5601
```


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

### PostgreSQL from the host (DBeaver)

After **`make port-forward-minikube`** or **`make port-forward-dockerdesktop-k8s`** (or **`.\scripts\port-forward-minikube.ps1`** / **`.\scripts\port-forward-dockerdesktop-k8s.ps1`**), connect from DBeaver or any SQL client:

| Field | Value |
| ----- | ----- |
| Host | `localhost` |
| Port | **`55432`** (local side of the forward; avoids conflicting with Docker Compose PostgreSQL on **`5432`**) |
| Database | `onlinestore` |
| Username | `onlinestore` |
| Password | `onlinestore` |

The Minikube / Kubernetes manifest does **not** run `database/aurora-postgres-schema.sql` on first start (unlike Docker Compose). If the database is empty, open **`database/aurora-postgres-schema.sql`** in DBeaver and execute it once against this connection.

**Optional:** pick different local ports by editing **`scripts/port-forward-minikube.ps1`** (or **`scripts/port-forward-dockerdesktop-k8s.ps1`**) or running your own `kubectl port-forward` mappings.

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
- waits for deployments to become ready (`postgres` and `redis` first, then the rest)
- starts **`scripts/port-forward-dockerdesktop-k8s.ps1`** automatically (same localhost map as Minikube, including **PostgreSQL `localhost:55432`** — see [PostgreSQL from the host (DBeaver)](#postgresql-from-the-host-dbeaver))

Final gateway URL after deploy:

- `http://localhost:5152`

**Checkout checklist:** if **`5152` refuses connections**, run **`make stop-port-forward-dockerdesktop-k8s`** then **`make port-forward-dockerdesktop-k8s`**, or **`make restart-port-forward-dockerdesktop-k8s`**. Confirm **`curl http://localhost:5152/health`**. Deploy uses **420s** rollout timeouts for Elasticsearch and Kibana, **240s** for other workloads.

**Checkout simulator:** **`make run-checkout-simulator-dockerdesktop-k8s`** (alias of **`make run-checkout-simulator-minikube`**) — same **`localhost:5152`** / **`4317`** mapping as **`scripts/port-forward-dockerdesktop-k8s.ps1`**. Uses **`appsettings.Minikube.json`** only as a *host port profile* name; it applies to Docker Desktop Kubernetes too. Kafka stays off (manifest has no broker); gateway must include the **`ReverseProxy` env-after-JSON fix** in **`Gateway/Program.cs`** — rebuild images with **`deploy-dockerdesktop-k8s`** after pulling latest sources.

**PostgreSQL / DBeaver:** **`make port-forward-dockerdesktop-k8s`** also forwards **`localhost:55432`** to the cluster database; use the same connection settings as in [PostgreSQL from the host (DBeaver)](#postgresql-from-the-host-dbeaver).

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

