using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Polly;
using Polly.Extensions.Http;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.AddOpenApi();

    var jitterer = new Random();
    void AddResilientClient(string name, string? baseUrl, string fallbackUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? fallbackUrl : baseUrl;
        builder.Services.AddHttpClient(name, client => { client.BaseAddress = new Uri(url); })
            .AddPolicyHandler(GetRetryPolicy(jitterer))
            .AddPolicyHandler(GetCircuitBreakerPolicy());
    }

    var config = builder.Configuration;
    AddResilientClient("OrderService", config["Services:OrderServiceBaseUrl"], "http://localhost:5240");
    AddResilientClient("PaymentService", config["Services:PaymentServiceBaseUrl"], "http://localhost:5031");
    AddResilientClient("ProductService", config["Services:ProductServiceBaseUrl"], "http://localhost:5225");
    AddResilientClient("CartService", config["Services:CartServiceBaseUrl"], "http://localhost:5078");
    AddResilientClient("UserService", config["Services:UserServiceBaseUrl"], "http://localhost:5121");
    AddResilientClient("InventoryService", config["Services:InventoryServiceBaseUrl"], "http://localhost:5212");
    AddResilientClient("ShippingService", config["Services:ShippingServiceBaseUrl"], "http://localhost:5219");
    AddResilientClient("HistoryService", config["Services:HistoryServiceBaseUrl"], "http://localhost:5029");

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("frontend", policy =>
        {
            policy.WithOrigins(
                    "https://localhost:5173",
                    "http://localhost:5173",
                    "https://127.0.0.1:5173",
                    "http://127.0.0.1:5173")
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
    });

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("checkout", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        options.AddPolicy("catalog", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseSerilogRequestLogging();
    app.UseCors("frontend");
    app.UseRateLimiter();

    app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "ok" }));

    app.MapGet("/api/products", async (string? q, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("ProductService");
        var path = string.IsNullOrWhiteSpace(q) ? "/products" : $"/products?q={Uri.EscapeDataString(q)}";
        var response = await client.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            body,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    })
    .RequireRateLimiting("catalog");

    app.MapGet("/api/carts/{userId:guid}", async (Guid userId, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("CartService");
        var response = await client.GetAsync($"/carts/{userId}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            body,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPut("/api/carts/{userId:guid}", async (Guid userId, HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("CartService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PutAsync($"/carts/{userId}", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            responseBody,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPost("/api/checkout", async (CheckoutRequest request, HttpContext context, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, CancellationToken ct) =>
    {
        var logger = loggerFactory.CreateLogger("Checkout");
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyValues))
        {
            return Results.BadRequest(new { error = "Missing Idempotency-Key header." });
        }

        var idempotencyKey = idempotencyKeyValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new { error = "Idempotency-Key header cannot be empty." });
        }

        var userClient = httpClientFactory.CreateClient("UserService");
        var cartClient = httpClientFactory.CreateClient("CartService");
        var orderClient = httpClientFactory.CreateClient("OrderService");
        var paymentClient = httpClientFactory.CreateClient("PaymentService");
        var inventoryClient = httpClientFactory.CreateClient("InventoryService");
        var shippingClient = httpClientFactory.CreateClient("ShippingService");
        var historyClient = httpClientFactory.CreateClient("HistoryService");

        var userResponse = await userClient.GetAsync($"/users/{request.UserId}", ct);
        if (userResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return Results.BadRequest(new { error = "User not found." });
        }

        if (!userResponse.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)userResponse.StatusCode);
        }

        var cartResponse = await cartClient.GetAsync($"/carts/{request.UserId}", ct);
        if (cartResponse.IsSuccessStatusCode)
        {
            var cart = await cartResponse.Content.ReadFromJsonAsync<CartDto>(cancellationToken: ct);
            if (cart is { Items.Count: > 0 } && !CartMatchesCheckout(cart.Items, request.Items))
            {
                return Results.BadRequest(new { error = "Checkout line items must match the saved cart." });
            }
        }

        var createOrderRequest = new CreateOrderRequest(request.UserId, request.Currency, request.Items);
        var orderResponse = await orderClient.PostAsJsonAsync("/orders", createOrderRequest, ct);
        if (!orderResponse.IsSuccessStatusCode)
        {
            return Results.StatusCode((int)orderResponse.StatusCode);
        }

        var order = await orderResponse.Content.ReadFromJsonAsync<OrderDto>(cancellationToken: ct);
        if (order is null)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        var reserved = new List<(Guid ProductId, int Qty)>();
        try
        {
            foreach (var line in request.Items)
            {
                var reserveResponse = await inventoryClient.PostAsync(
                    $"/inventory/{line.ProductId}/reserve?quantity={line.Quantity}",
                    content: null,
                    ct);
                if (!reserveResponse.IsSuccessStatusCode)
                {
                    await ReleaseInventoryReservationsAsync(inventoryClient, reserved, ct);
                    await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
                    if (reserveResponse.StatusCode == HttpStatusCode.BadRequest)
                    {
                        return Results.BadRequest(new { error = "Insufficient inventory for one or more products." });
                    }

                    return Results.StatusCode((int)reserveResponse.StatusCode);
                }

                reserved.Add((line.ProductId, line.Quantity));
            }

            var paymentRequest = new AuthorizePaymentRequest(
                order.OrderId,
                order.TotalAmount,
                order.Currency,
                PaymentMethodToken: "pm_card_visa");

            using var paymentMessage = new HttpRequestMessage(HttpMethod.Post, "/payments/authorize")
            {
                Content = JsonContent.Create(paymentRequest)
            };
            paymentMessage.Headers.Add("Idempotency-Key", idempotencyKey);

            var paymentResponse = await paymentClient.SendAsync(paymentMessage, ct);
            if (!paymentResponse.IsSuccessStatusCode)
            {
                await ReleaseInventoryReservationsAsync(inventoryClient, reserved, ct);
                await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
                return Results.StatusCode((int)paymentResponse.StatusCode);
            }

            var payment = await paymentResponse.Content.ReadFromJsonAsync<PaymentDto>(cancellationToken: ct);
            if (payment is null)
            {
                await ReleaseInventoryReservationsAsync(inventoryClient, reserved, ct);
                await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            await orderClient.PostAsync($"/orders/{order.OrderId}/complete", content: null, ct);

            foreach (var line in request.Items)
            {
                var commitResponse = await inventoryClient.PostAsync(
                    $"/inventory/{line.ProductId}/commit?quantity={line.Quantity}",
                    content: null,
                    ct);
                if (!commitResponse.IsSuccessStatusCode)
                {
                    logger.LogWarning(
                        "Inventory commit returned {Status} for product {ProductId} order {OrderId}",
                        commitResponse.StatusCode,
                        line.ProductId,
                        order.OrderId);
                }
            }

            var shipmentResponse = await shippingClient.PostAsync($"/shipments/{order.OrderId}", content: null, ct);
            ShipmentDto? shipment = null;
            if (shipmentResponse.IsSuccessStatusCode)
            {
                shipment = await shipmentResponse.Content.ReadFromJsonAsync<ShipmentDto>(cancellationToken: ct);
            }
            else
            {
                logger.LogWarning("Shipping service returned {Status} for order {OrderId}", shipmentResponse.StatusCode, order.OrderId);
            }

            var historyEvent = new OrderHistoryEventDto(
                Guid.Empty,
                order.OrderId,
                request.UserId,
                "CheckoutCompleted",
                default,
                $"Payment {payment.ProviderReference}; total {order.TotalAmount} {order.Currency}");
            await historyClient.PostAsJsonAsync("/history/events", historyEvent, ct);

            await cartClient.DeleteAsync($"/carts/{request.UserId}", ct);

            logger.LogInformation(
                "[EmailStub] Would send order confirmation email for order {OrderId} to user {UserId}",
                order.OrderId,
                request.UserId);

            return Results.Ok(new
            {
                order,
                payment,
                shipment,
                status = "CheckoutCompleted"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checkout failed after order {OrderId} was created", order.OrderId);
            await ReleaseInventoryReservationsAsync(inventoryClient, reserved, ct);
            await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
            throw;
        }
    })
    .RequireRateLimiting("checkout");

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Gateway host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static bool CartMatchesCheckout(IReadOnlyList<CartItemDto> cart, IReadOnlyList<CartItemDto> checkout)
{
    if (cart.Count != checkout.Count)
    {
        return false;
    }

    var cartByProduct = cart.ToDictionary(x => x.ProductId, x => x.Quantity);
    foreach (var item in checkout)
    {
        if (!cartByProduct.TryGetValue(item.ProductId, out var qty) || qty != item.Quantity)
        {
            return false;
        }
    }

    return true;
}

static async Task ReleaseInventoryReservationsAsync(HttpClient inventoryClient, List<(Guid ProductId, int Qty)> reserved, CancellationToken ct)
{
    foreach (var (productId, qty) in reserved)
    {
        await inventoryClient.PostAsync($"/inventory/{productId}/release?quantity={qty}", content: null, ct);
    }
}

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(Random random) =>
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))
                + TimeSpan.FromMilliseconds(random.Next(0, 200)));

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
    HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 3,
            durationOfBreak: TimeSpan.FromSeconds(30));
