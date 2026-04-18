# Online Store Production Blueprint

This blueprint aligns your stack: ASP.NET microservices, React storefront, AWS Fargate, Aurora PostgreSQL, Stripe.

## 1) Core architecture

- Frontend: React app calls one entry point (`API Gateway` or `ALB + BFF`).
- Services: `Product`, `Cart`, `Order`, `Payment`, `Inventory`, `Shipping`, `User`, `History`.
- Data: Aurora PostgreSQL (schema-per-service) + Redis for cart/cache.
- Messaging: SNS/SQS for async events and decoupled workflows.

## 2) Checkout saga (orchestration-first)

Recommended for AWS: orchestrate checkout with AWS Step Functions.

1. Create order (Pending)
2. Reserve inventory
3. Authorize payment (Stripe)
4. Mark order paid/completed
5. Create shipment

Compensations on failure:

- Payment fails -> release inventory -> mark order failed.
- Inventory fails -> cancel order.

Use choreography only when workflow is simple; use orchestration for visibility and controlled rollback.

## 3) Reliability patterns in .NET

Apply to all downstream HTTP integrations:

- Timeout: fail fast for stuck dependencies.
- Retry: transient failures only (5xx, timeout, network) with exponential backoff + jitter.
- Circuit breaker: open circuit after repeated failure to stop cascading outages.
- Bulkhead (optional): isolate high-risk dependencies behind separate client pools.

Use Polly per named `HttpClient`.

## 4) Payment safety and idempotency

For `/checkout` and payment endpoints:

- Require `Idempotency-Key` header from frontend.
- Persist request hash + response payload.
- Return stored response if key already exists.
- Reject same key with different payload.
- Pass the same idempotency key to Stripe request options.

This prevents duplicate charges from retries, timeout retries, or user double-clicks.

## 5) API Gateway rate limiting

Protect expensive and abuse-prone routes:

- `POST /checkout`: strict limit (for example 5/min/user).
- `POST /payments/*`: strict limit and WAF bot rules.
- `GET /products`: higher burst and sustained limits.

Enforce by user identity when authenticated, otherwise fallback to client IP.

## 6) Blue/Green on ECS Fargate

Use ECS + CodeDeploy with two target groups behind ALB:

- Blue = current production task set
- Green = new task set
- Deploy to green, run health checks/smoke tests, then shift traffic.
- Rollback by routing traffic back to blue instantly.

Suggested traffic shift:

- 10% for 5 minutes
- 50% for 10 minutes
- 100% when metrics are healthy

Monitor before full cutover:

- p95 latency
- 5xx rate
- payment authorization success rate
- checkout completion rate

## 7) Observability baseline

- Logs: Serilog structured logs with trace/span correlation IDs.
- Traces: OpenTelemetry with Jaeger (or AWS X-Ray if preferred).
- Metrics: Prometheus + Grafana dashboards for checkout funnel.
- Alerts: error budget burn, payment failure spike, queue backlog growth.

## 8) Immediate implementation checklist

1. Apply SQL in `database/aurora-postgres-schema.sql`.
2. Integrate patterns from `dotnet/ResilienceAndCheckoutSample.cs` into your services.
3. Add Step Functions state machine for checkout orchestration.
4. Enforce API gateway throttling + WAF rules.
5. Configure ECS CodeDeploy blue/green with automatic rollback alarms.
