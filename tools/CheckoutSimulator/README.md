# Checkout simulator

Console app that drives a **full checkout** through the **gateway** (login → catalog → cart → inventory reserve → order → payment → inventory commit → order complete → shipment → history), then publishes an **`EmailNotificationRequestedEvent`** to **Kafka** so `email-service` consumes it.

## Prerequisites

- **Docker Compose:** `docker compose up --build` from the repo root (gateway published as **`localhost:8081`**, Jaeger UI **`localhost:16686`**, OTLP **`localhost:4317`**, Kafka **`localhost:9092`**).
- **Kubernetes on the host (Minikube or Docker Desktop K8s):** Deploy (`deploy-minikube.ps1` or **`deploy-dockerdesktop-k8s.ps1`** with **`kubectl config use-context docker-desktop`**), then port-forward or run **`make run-checkout-simulator-minikube`** / **`make run-checkout-simulator-dockerdesktop-k8s`** (same script — gateway **`localhost:5152`**, OTLP **`4317`**). The bundled manifests have no Kafka unless you add it.

## Docker Compose vs Minikube

### Switch by gateway port

The simulator only needs **`Gateway:BaseUrl`** to aim at the right listener. Defaults match this repo’s published ports; change the URL when your gateway listens elsewhere (different compose mapping, NodePort, or another local port).

| | Gateway URL | OTLP (Jaeger collector) | Kafka publish |
|--|-------------|-------------------------|----------------|
| **Docker Compose** | `http://localhost:8081/` | `http://localhost:4317` | **On** (`Simulator:PublishKafka` default `true` in `appsettings.json`) |
| **Minikube** (script / `DOTNET_ENVIRONMENT=Minikube`) | `http://localhost:5152/` | `http://localhost:4317` | **Off** in `appsettings.Minikube.json` (no broker in bundle) |

### Run against Docker Compose (default)

Stack up, then:

```powershell
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

Or from repo root:

```powershell
make run-checkout-simulator-docker
```

(`make run-checkout-simulator` is the same target.)

Uses **`appsettings.json`** (`Production`): gateway **8081**, OTLP **4317**, Kafka **9092**.

To force the same URLs explicitly:

```powershell
$env:Gateway__BaseUrl = 'http://localhost:8081/'
$env:Observability__OtlpEndpoint = 'http://localhost:4317'
$env:Simulator__PublishKafka = 'true'
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

Linux/macOS:

```bash
export Gateway__BaseUrl='http://localhost:8081/'
export Observability__OtlpEndpoint='http://localhost:4317'
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

### Run against Minikube

Prefer loading **`appsettings.Minikube.json`** so gateway **5152**, retries, and Kafka-off stay aligned:

```powershell
.\scripts\run-checkout-simulator-minikube.ps1
```

Or `make run-checkout-simulator-minikube`.

Manual equivalent:

```powershell
$env:DOTNET_ENVIRONMENT = 'Minikube'
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

If the gateway port-forward died but Minikube is still up: `make restart-port-forward-minikube`, then rerun the simulator.

### Switch stacks by changing only the gateway port

You do **not** have to edit JSON files if you set **`Gateway__BaseUrl`** (always include the trailing slash).

**Minikube-style gateway on 5152** without loading the Minikube profile (you must turn Kafka off yourself):

```powershell
$env:Gateway__BaseUrl = 'http://localhost:5152/'
$env:Observability__OtlpEndpoint = 'http://localhost:4317'
$env:Simulator__PublishKafka = 'false'
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

**Docker Compose but gateway mapped to another host port** (example **18081**):

```powershell
$env:Gateway__BaseUrl = 'http://localhost:18081/'
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

**Arbitrary port-forward** (example **5555:8080**):

```powershell
$env:Gateway__BaseUrl = 'http://localhost:5555/'
dotnet run --project tools/CheckoutSimulator/CheckoutSimulator.csproj
```

Environment variables override both `appsettings.json` and `appsettings.Minikube.json`.

## Run (from simulator project folder)

```powershell
cd tools/CheckoutSimulator
dotnet run
```

Same defaults as Docker Compose (**8081**) unless you set **`DOTNET_ENVIRONMENT`** / **`Gateway__BaseUrl`** as above.

## Configuration

| Key | Default | Description |
|-----|---------|-------------|
| Environment | `Production` | Set **`DOTNET_ENVIRONMENT=Minikube`** (or use `run-checkout-simulator-minikube.ps1`) to load **`appsettings.Minikube.json`** |
| `Gateway:BaseUrl` | `http://localhost:8081/` (`5152/` in Minikube file) | Gateway base URL |
| `Observability:OtlpEndpoint` | `http://localhost:4317` | Set empty to disable OTLP (simulator traces only) |
| `Messaging:Kafka:BootstrapServers` | `localhost:9092` | Kafka bootstrap |
| `Messaging:Kafka:Topic` | `email-notifications` | Topic for email events |
| `Simulator:PublishKafka` | `true` | Set `false` to skip Kafka publish |
| `Simulator:GatewayWaitSeconds` | `120` | Poll `/health` until gateway responds (connection refused / cold start) |
| `Simulator:GatewayPollSeconds` | `2` | Delay between readiness attempts |
| `Simulator:HttpRetries` | `6` | Retries per request on transient tunnel errors (`ResponseEnded`, truncated body) |
| `Simulator:HttpRetryDelayMs` | `400` | Base backoff; delay = base × attempt |

The gateway client uses **HTTP/1.1 only** (`kubectl port-forward` often breaks HTTP/2 with **response ended prematurely**).

The host uses **`AppContext.BaseDirectory`** as **content root**, so **`appsettings.*.json` next to the built DLL** load correctly even when you run `dotnet run --project tools/CheckoutSimulator/...` from the repo root.

The **`Minikube:*`** keys (Jaeger UI, Zipkin, Redis, direct service HTTP bases, Grafana, etc.) are reference URLs only; HTTP traffic stays on **`Gateway:BaseUrl`**. Override any setting with environment variables (for example `Gateway__BaseUrl=http://localhost:30081/` if you use a NodePort).

### Troubleshooting

- **`Connection refused` on port 5152 (Minikube):** Nothing is listening — the gateway port-forward is missing or exited. Run `make port-forward-minikube` or `kubectl port-forward -n online-store svc/gateway 5152:8080`. If deploy printed **Skipping gateway: localhost:5152 already in use**, an old forward or another process holds the port — run `scripts/stop-port-forward-minikube.ps1`, then start forwards again.
- The simulator waits for **`GET /health`** before login (`Simulator:GatewayWaitSeconds`).

## What to watch

- **Jaeger** (`http://localhost:16686`): traces for `checkout-simulator` (outbound HTTP) and downstream services (`gateway`, `order-service`, …).
- **Kafka / email-service**: container logs for `email-service` after the publish step (topic `email-notifications`).

Credentials: `demo@example.com` / `demo-password` (same as the seeded demo user).
