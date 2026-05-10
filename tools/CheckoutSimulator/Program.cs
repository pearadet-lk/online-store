using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    ContentRootPath = AppContext.BaseDirectory,
    Args = args
});

var gatewayBase = builder.Configuration["Gateway:BaseUrl"] ?? "http://localhost:8081/";
if (!gatewayBase.EndsWith("/", StringComparison.Ordinal))
{
    gatewayBase += "/";
}

builder.Services.AddHttpClient("gateway", client =>
{
    client.BaseAddress = new Uri(gatewayBase, UriKind.Absolute);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestVersion = HttpVersion.Version11;
    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(1)
});

var otlp = builder.Configuration["Observability:OtlpEndpoint"];
if (!string.IsNullOrWhiteSpace(otlp))
{
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("checkout-simulator"))
        .WithTracing(t => t
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));
}

builder.Services.AddHostedService<CheckoutRunner>();

var host = builder.Build();
await host.RunAsync();

internal sealed class CheckoutRunner(
    IHttpClientFactory httpFactory,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    IHostEnvironment hostEnvironment,
    ILogger<CheckoutRunner> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunCheckoutAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checkout simulation failed.");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    private async Task RunCheckoutAsync(CancellationToken ct)
    {
        LogMinikubeEndpointSummary();

        var http = httpFactory.CreateClient("gateway");
        await WaitForGatewayAsync(http, ct);

        const string email = "demo@example.com";
        const string password = "demo-password";

        logger.LogInformation("Logging in as {Email}...", email);
        using var loginResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsJsonAsync("/api/users/login", new { email, password }, Json, ct),
            "login",
            ct);
        loginResponse.EnsureSuccessStatusCode();
        await using var loginStream = await loginResponse.Content.ReadAsStreamAsync(ct);
        using var loginDoc = await JsonDocument.ParseAsync(loginStream, cancellationToken: ct);
        var root = loginDoc.RootElement;
        var token = root.GetProperty("accessToken").GetString()
            ?? throw new InvalidOperationException("Missing accessToken.");
        var userId = root.GetProperty("user").GetProperty("userId").GetGuid();
        var customerEmail = root.GetProperty("user").GetProperty("email").GetString() ?? email;
        var customerName = root.GetProperty("user").TryGetProperty("fullName", out var fn) && fn.ValueKind == JsonValueKind.String
            ? fn.GetString() ?? "Demo User"
            : "Demo User";

        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        logger.LogInformation("Loading products...");
        using var productsResponse = await SendWithTunnelRetryAsync(() => http.GetAsync("/api/products", ct), "GET products", ct);
        productsResponse.EnsureSuccessStatusCode();
        var products = await productsResponse.Content.ReadFromJsonAsync<List<ProductJson>>(Json, ct);
        if (products is null || products.Count == 0)
        {
            throw new InvalidOperationException("No products returned from catalog.");
        }

        var product = products[0];
        var quantity = 1;
        logger.LogInformation("Using product {ProductId} ({Name}) qty {Qty}", product.ProductId, product.Name, quantity);

        logger.LogInformation("Updating cart for user {UserId}...", userId);
        using var cartResponse = await SendWithTunnelRetryAsync(
            () => http.PutAsJsonAsync(
                $"/api/carts/{userId}",
                new { userId, items = new[] { new { productId = product.ProductId, quantity, unitPrice = product.Price } } },
                Json,
                ct),
            "PUT cart",
            ct);
        cartResponse.EnsureSuccessStatusCode();

        logger.LogInformation("Reserving inventory...");
        using var reserveResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsync(
                $"/api/inventory/{product.ProductId}/reserve?quantity={quantity}",
                null,
                ct),
            "inventory reserve",
            ct);
        reserveResponse.EnsureSuccessStatusCode();

        logger.LogInformation("Creating order...");
        using var orderResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsJsonAsync(
                "/api/orders",
                new { userId, currency = "USD", items = new[] { new { productId = product.ProductId, quantity, unitPrice = product.Price } } },
                Json,
                ct),
            "create order",
            ct);
        orderResponse.EnsureSuccessStatusCode();
        var order = await orderResponse.Content.ReadFromJsonAsync<OrderJson>(Json, ct)
            ?? throw new InvalidOperationException("Invalid order response.");
        logger.LogInformation("Order created: {OrderId} total {Total}", order.OrderId, order.TotalAmount);

        logger.LogInformation("Authorizing payment...");
        using var payResponse = await SendWithTunnelRetryAsync(
            () => SendPaymentAuthorizeAsync(http, order.OrderId, order.TotalAmount, ct),
            "payment authorize",
            ct);
        payResponse.EnsureSuccessStatusCode();

        logger.LogInformation("Committing inventory...");
        using var commitResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsync(
                $"/api/inventory/{product.ProductId}/commit?quantity={quantity}",
                null,
                ct),
            "inventory commit",
            ct);
        commitResponse.EnsureSuccessStatusCode();

        logger.LogInformation("Completing order...");
        using var completeResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsync($"/api/orders/{order.OrderId}/complete", null, ct),
            "order complete",
            ct);
        completeResponse.EnsureSuccessStatusCode();

        logger.LogInformation("Creating shipment...");
        using var shipResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsync($"/api/shipments/{order.OrderId}", null, ct),
            "create shipment",
            ct);
        shipResponse.EnsureSuccessStatusCode();

        logger.LogInformation("Recording history event...");
        using var historyResponse = await SendWithTunnelRetryAsync(
            () => http.PostAsJsonAsync(
                "/api/history/events",
                new
                {
                    historyId = Guid.Empty,
                    orderId = order.OrderId,
                    userId,
                    eventType = "CheckoutCompleted",
                    createdAt = DateTimeOffset.UtcNow,
                    notes = "checkout-simulator"
                },
                Json,
                ct),
            "history event",
            ct);
        historyResponse.EnsureSuccessStatusCode();

        var publishKafka = configuration.GetValue("Simulator:PublishKafka", true);
        if (publishKafka)
        {
            var bootstrap = configuration["Messaging:Kafka:BootstrapServers"] ?? "localhost:9092";
            var topic = configuration["Messaging:Kafka:Topic"] ?? "email-notifications";
            var evt = new EmailNotificationRequestedEvent(
                Guid.NewGuid(),
                order.OrderId,
                userId,
                customerEmail,
                customerName,
                order.TotalAmount,
                order.Currency,
                DateTimeOffset.UtcNow);

            var payload = JsonSerializer.Serialize(evt, Json);
            logger.LogInformation("Publishing email notification to Kafka topic {Topic}...", topic);

            using var producer = new ProducerBuilder<Null, string>(new ProducerConfig
            {
                BootstrapServers = bootstrap,
                Acks = Acks.Leader
            }).Build();

            await producer.ProduceAsync(topic, new Message<Null, string> { Value = payload }, ct);
            producer.Flush(TimeSpan.FromSeconds(10));
            logger.LogInformation("Kafka message delivered. Watch email-service logs for consumer processing.");
        }
        else
        {
            logger.LogInformation("Kafka publish skipped (Simulator:PublishKafka=false).");
        }

        var traceId = Activity.Current?.TraceId.ToString() ?? "(no active span; check OTLP endpoint)";
        logger.LogInformation("Done. Search Jaeger for traceId {TraceId} or gateway X-Trace-Id header on prior requests.", traceId);
    }

    private void LogMinikubeEndpointSummary()
    {
        if (!string.Equals(hostEnvironment.EnvironmentName, "Minikube", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        logger.LogInformation(
            "Minikube profile: Gateway={Gateway}; OTLP={Otlp}; PublishKafka={Kafka}. Checkout traffic uses the gateway only.",
            configuration["Gateway:BaseUrl"],
            configuration["Observability:OtlpEndpoint"],
            configuration["Simulator:PublishKafka"]);

        logger.LogInformation(
            "Minikube port-forward reference (scripts/port-forward-minikube.ps1): Jaeger UI {Jaeger}; Zipkin {Zipkin}; Redis {Redis}; Prometheus {Prom}; Grafana {Grafana}; Elasticsearch {Es}; Kibana {Kibana}.",
            configuration["Minikube:JaegerUi"],
            configuration["Minikube:ZipkinUi"],
            configuration["Minikube:Redis"],
            configuration["Minikube:Prometheus"],
            configuration["Minikube:Grafana"],
            configuration["Minikube:Elasticsearch"],
            configuration["Minikube:Kibana"]);

        logger.LogInformation(
            "Minikube direct service bases (optional debugging; normal flow is via gateway): order={Order}; payment={Payment}; product={Product}; cart={Cart}; user={User}; inventory={Inventory}; shipping={Shipping}; history={History}.",
            configuration["Minikube:OrderService"],
            configuration["Minikube:PaymentService"],
            configuration["Minikube:ProductService"],
            configuration["Minikube:CartService"],
            configuration["Minikube:UserService"],
            configuration["Minikube:InventoryService"],
            configuration["Minikube:ShippingService"],
            configuration["Minikube:HistoryService"]);
    }

    private async Task WaitForGatewayAsync(HttpClient http, CancellationToken ct)
    {
        var totalSeconds = Math.Clamp(configuration.GetValue("Simulator:GatewayWaitSeconds", 120), 5, 600);
        var pollSeconds = Math.Clamp(configuration.GetValue("Simulator:GatewayPollSeconds", 2), 1, 60);
        var deadline = DateTime.UtcNow.AddSeconds(totalSeconds);
        var attempt = 0;

        var healthProbeUrl = $"{http.BaseAddress!.AbsoluteUri.TrimEnd('/')}/health";
        logger.LogInformation(
            "Waiting up to {Seconds}s for gateway at {HealthUrl} ...",
            totalSeconds,
            healthProbeUrl);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                using var response = await SendWithTunnelRetryAsync(
                    () => http.GetAsync("/health", ct),
                    "gateway health",
                    ct);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation("Gateway reachable after {Attempts} attempt(s).", attempt);
                    return;
                }

                logger.LogWarning("Gateway /health returned {Status}; retry in {Poll}s.", response.StatusCode, pollSeconds);
            }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "Gateway not reachable (attempt {Attempt}); retry in {Poll}s. Ensure port-forward is running (e.g. kubectl port-forward -n online-store svc/gateway 5152:8080).",
                    attempt,
                    pollSeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), ct);
        }

        throw new InvalidOperationException(
            $"Gateway did not respond at {http.BaseAddress} within {totalSeconds}s. " +
            "Minikube / k8s: run `scripts/port-forward-minikube.ps1` or `make port-forward-minikube` after deploy. " +
            "If the script skipped gateway because the port was busy, run `scripts/stop-port-forward-minikube.ps1` then forward again. " +
            "Docker Compose: gateway is usually http://localhost:8081/.");
    }

    private async Task<HttpResponseMessage> SendWithTunnelRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        string operation,
        CancellationToken ct)
    {
        var max = Math.Clamp(configuration.GetValue("Simulator:HttpRetries", 6), 1, 30);
        var delayMs = Math.Clamp(configuration.GetValue("Simulator:HttpRetryDelayMs", 400), 50, 30000);

        for (var attempt = 1; attempt <= max; attempt++)
        {
            try
            {
                return await send();
            }
            catch (HttpRequestException ex) when (IsTransientTunnelFailure(ex))
            {
                if (attempt >= max)
                {
                    throw;
                }

                logger.LogWarning(
                    ex,
                    "{Operation}: transient HTTP failure ({Attempt}/{Max}); retry in {Delay}ms (typical with kubectl port-forward).",
                    operation,
                    attempt,
                    max,
                    delayMs * attempt);
                await Task.Delay(delayMs * attempt, ct);
            }
        }

        throw new InvalidOperationException($"{operation}: exhausted retries.");
    }

    private Task<HttpResponseMessage> SendPaymentAuthorizeAsync(
        HttpClient http,
        Guid orderId,
        decimal totalAmount,
        CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/payments/authorize");
        req.Headers.TryAddWithoutValidation("Idempotency-Key", $"sim-pay-{orderId:N}");
        req.Content = JsonContent.Create(
            new
            {
                orderId,
                amount = totalAmount,
                currency = "USD",
                paymentMethodToken = "pm_simulator_mock"
            },
            options: Json);
        return http.SendAsync(req, ct);
    }

    private static bool IsTransientTunnelFailure(HttpRequestException ex)
    {
        for (var e = ex.InnerException; e != null; e = e.InnerException)
        {
            if (e is HttpIOException)
            {
                return true;
            }

            if (string.Equals(e.GetType().Name, "HttpIOException", StringComparison.Ordinal))
            {
                return true;
            }

            if (e is SocketException se)
            {
                return IsTransientSocketError(se.SocketErrorCode);
            }
        }

        var blob = ex.Message;
        if (ex.InnerException != null)
        {
            blob += ex.InnerException.Message;
        }

        return blob.Contains("prematurely", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("ResponseEnded", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("refused", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("connection aborted", StringComparison.OrdinalIgnoreCase)
               || blob.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTransientSocketError(SocketError code) =>
        code is SocketError.ConnectionRefused
            or SocketError.ConnectionReset
            or SocketError.ConnectionAborted
            or SocketError.TimedOut
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable;
}

internal sealed record ProductJson(Guid ProductId, string Name, decimal Price);

internal sealed record OrderJson(
    [property: JsonPropertyName("orderId")] Guid OrderId,
    [property: JsonPropertyName("totalAmount")] decimal TotalAmount,
    [property: JsonPropertyName("currency")] string Currency);
