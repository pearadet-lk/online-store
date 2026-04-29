using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Confluent.Kafka;
using Contracts;
using Contracts.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.RateLimiting;
using Gateway;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Polly.CircuitBreaker;
using Polly.Extensions.Http;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;

const string ServiceName = "gateway";
const string DefaultApiVersion = "v1";

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        var elasticsearchUrl = context.Configuration["Observability:ElasticsearchUrl"] ?? "http://elasticsearch:9200";
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", ServiceName)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [TraceId:{TraceId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticsearchUrl))
            {
                AutoRegisterTemplate = true,
                IndexFormat = $"online-store-{ServiceName}-logs-{DateTime.UtcNow:yyyy.MM}"
            });
    });
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(ServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(builder.Configuration["Observability:OtlpEndpoint"] ?? "http://jaeger:4317");
            }));
    builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
    builder.Services.AddSingleton<EmailNotificationPublisher>();
    builder.Services.AddSingleton<CheckoutSagaStore>();
    var idempotencyPostgresConnection = builder.Configuration["Idempotency:PostgresConnectionString"];
    if (!string.IsNullOrWhiteSpace(idempotencyPostgresConnection))
    {
        builder.Services.AddSingleton(_ =>
            new NpgsqlDataSourceBuilder(idempotencyPostgresConnection).Build());
        builder.Services.AddSingleton<ICheckoutIdempotencyStore, PostgresCheckoutIdempotencyStore>();
    }
    else
    {
        var idempotencyRedisConnection = builder.Configuration["Idempotency:RedisConnectionString"];
        if (!string.IsNullOrWhiteSpace(idempotencyRedisConnection))
        {
            builder.Services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = idempotencyRedisConnection;
                options.InstanceName = "online-store:";
            });
        }
        else
        {
            builder.Services.AddDistributedMemoryCache();
        }

        builder.Services.AddSingleton<ICheckoutIdempotencyStore, DistributedCacheCheckoutIdempotencyStore>();
    }
    builder.Services.AddSingleton<IAsyncPolicy<InventoryOperationReply>>(serviceProvider =>
    {
        var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("GatewayInventoryGrpcPolly");
        return InventoryGrpcResilience.BuildInventoryGrpcPolicy(logger);
    });

    var jitterer = new Random();
    void AddResilientClient(string name, string? baseUrl, string fallbackUrl)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? fallbackUrl : baseUrl;
        builder.Services.AddHttpClient(name, client => { client.BaseAddress = new Uri(url); })
            .AddPolicyHandler(GetRetryPolicy(jitterer))
            .AddPolicyHandler(GetCircuitBreakerPolicy());
    }

    var config = builder.Configuration;
    builder.Services.AddSingleton(_ => new InventoryGrpcCallSettings(
        TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Services:InventoryGrpcTimeoutSeconds", 3), 1, 30))));
    AddResilientClient("OrderService", config["Services:OrderServiceBaseUrl"], "http://localhost:5240");
    AddResilientClient("PaymentService", config["Services:PaymentServiceBaseUrl"], "http://localhost:5031");
    AddResilientClient("ProductService", config["Services:ProductServiceBaseUrl"], "http://localhost:5225");
    AddResilientClient("CartService", config["Services:CartServiceBaseUrl"], "http://localhost:5078");
    AddResilientClient("UserService", config["Services:UserServiceBaseUrl"], "http://localhost:5121");
    AddResilientClient("ShippingService", config["Services:ShippingServiceBaseUrl"], "http://localhost:5219");
    AddResilientClient("HistoryService", config["Services:HistoryServiceBaseUrl"], "http://localhost:5029");
    builder.Services
        .AddGrpcClient<InventoryGrpc.InventoryGrpcClient>(options =>
        {
            options.Address = new Uri(config["Services:InventoryServiceGrpcUrl"] ?? "http://localhost:5212");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true
        });

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
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseGlobalExceptionHandling(ServiceName);

    app.Use(async (context, next) =>
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-Id"] = traceId;
        using (LogContext.PushProperty("TraceId", traceId))
        {
            await next();
        }
    });
    app.UseSerilogRequestLogging();
    app.UseHttpMetrics();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments($"/api/{DefaultApiVersion}", out var remaining))
    {
        context.Request.Path = remaining.HasValue ? remaining : "/";
    }

    context.Response.Headers["api-supported-versions"] = DefaultApiVersion;
    await next();
});
    app.UseCors("frontend");
    app.UseRateLimiter();

    app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "ok" }));
    app.MapMetrics("/metrics");
    app.MapGet("/api/checkout/sagas/{sagaId:guid}", (Guid sagaId, CheckoutSagaStore sagaStore) =>
        sagaStore.States.TryGetValue(sagaId, out var state)
            ? Results.Ok(state)
            : Results.NotFound());

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

    app.MapPost("/api/users/login", async (HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("UserService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PostAsync("/users/login", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            responseBody,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPost("/api/users/register", async (HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("UserService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PostAsync("/users/register", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            responseBody,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPost("/api/users/refresh", async (HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("UserService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PostAsync("/users/refresh", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            responseBody,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPost("/api/users/logout", async (HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("UserService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PostAsync("/users/logout", content, ct);
        return Results.StatusCode((int)response.StatusCode);
    });

    app.MapGet("/api/admin/products", async (string? q, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("ProductService");
        var path = string.IsNullOrWhiteSpace(q) ? "/admin/products" : $"/admin/products?q={Uri.EscapeDataString(q)}";
        var response = await client.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            body,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPost("/api/admin/products", async (HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("ProductService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PostAsync("/products", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            responseBody,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapPut("/api/admin/products/{productId:guid}", async (Guid productId, HttpRequest incoming, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("ProductService");
        using var reader = new StreamReader(incoming.Body);
        var body = await reader.ReadToEndAsync(ct);
        using var content = new StringContent(body, Encoding.UTF8, incoming.ContentType ?? "application/json");
        var response = await client.PutAsync($"/products/{productId}", content, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            responseBody,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

    app.MapDelete("/api/admin/products/{productId:guid}", async (Guid productId, IHttpClientFactory httpClientFactory, CancellationToken ct) =>
    {
        var client = httpClientFactory.CreateClient("ProductService");
        var response = await client.DeleteAsync($"/products/{productId}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return Results.Content(
            body,
            contentType: response.Content.Headers.ContentType?.MediaType ?? "application/json",
            statusCode: (int)response.StatusCode);
    });

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

    app.MapPost("/api/checkout", async (
        CheckoutRequest request,
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        InventoryGrpc.InventoryGrpcClient inventoryClient,
        IAsyncPolicy<InventoryOperationReply> inventoryPolicy,
        InventoryGrpcCallSettings inventoryGrpcSettings,
        CheckoutSagaStore sagaStore,
        ICheckoutIdempotencyStore checkoutIdempotencyStore,
        ILoggerFactory loggerFactory,
        EmailNotificationPublisher emailPublisher,
        IConfiguration configuration,
        CancellationToken ct) =>
    {
        var logger = loggerFactory.CreateLogger("CheckoutSaga");
        if (!TryGetAuthorizedUserId(context, configuration, out var authenticatedUserId))
        {
            return Results.Unauthorized();
        }

        if (authenticatedUserId != request.UserId)
        {
            return Results.Forbid();
        }

        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyValues))
        {
            return Results.BadRequest(new { error = "Missing Idempotency-Key header." });
        }

        var idempotencyKey = idempotencyKeyValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new { error = "Idempotency-Key header cannot be empty." });
        }

        var requestHash = ComputeCheckoutPayloadHash(request);
        var scopedIdempotencyKey = $"{request.UserId:N}:{idempotencyKey}";
        var acquireResult = await checkoutIdempotencyStore.TryAcquireAsync(scopedIdempotencyKey, requestHash, ct);
        if (!acquireResult.Acquired)
        {
            if (acquireResult.PayloadMismatch)
            {
                return Results.Conflict(new { error = "Idempotency key was reused with a different checkout payload." });
            }

            if (acquireResult.InProgress)
            {
                return Results.Conflict(new { error = "Checkout with this idempotency key is already processing." });
            }

            if (!string.IsNullOrWhiteSpace(acquireResult.ResponseBody))
            {
                context.Response.Headers["X-Idempotent-Replay"] = "true";
                return Results.Content(
                    acquireResult.ResponseBody,
                    contentType: "application/json",
                    statusCode: acquireResult.ResponseStatusCode ?? StatusCodes.Status200OK);
            }
        }

        async Task<IResult> FailIdempotentAsync(IResult result)
        {
            await checkoutIdempotencyStore.MarkFailedAsync(scopedIdempotencyKey, ct);
            return result;
        }

        async Task<IResult> CompleteIdempotentAsync(int statusCode, object payload)
        {
            var payloadJson = JsonSerializer.Serialize(payload);
            await checkoutIdempotencyStore.MarkCompletedAsync(scopedIdempotencyKey, statusCode, payloadJson, ct);
            return Results.Content(payloadJson, contentType: "application/json", statusCode: statusCode);
        }

        try
        {
        var sagaId = Guid.NewGuid();
        sagaStore.States[sagaId] = new CheckoutSagaState(sagaId, request.UserId, null, "Started", DateTimeOffset.UtcNow, null);
        void UpdateSaga(string step, Guid? orderId = null, string? error = null) =>
            sagaStore.States[sagaId] = new CheckoutSagaState(sagaId, request.UserId, orderId, step, DateTimeOffset.UtcNow, error);

        var userClient = httpClientFactory.CreateClient("UserService");
        var cartClient = httpClientFactory.CreateClient("CartService");
        var orderClient = httpClientFactory.CreateClient("OrderService");
        var paymentClient = httpClientFactory.CreateClient("PaymentService");
        var shippingClient = httpClientFactory.CreateClient("ShippingService");
        var historyClient = httpClientFactory.CreateClient("HistoryService");

        var userResponse = await userClient.GetAsync($"/users/{request.UserId}", ct);
        if (userResponse.StatusCode == HttpStatusCode.NotFound)
        {
            UpdateSaga("Failed", error: "User not found");
            return await FailIdempotentAsync(Results.BadRequest(new { error = "User not found." }));
        }

        if (!userResponse.IsSuccessStatusCode)
        {
            UpdateSaga("Failed", error: "User service unavailable");
            return await FailIdempotentAsync(Results.StatusCode((int)userResponse.StatusCode));
        }

        var userProfile = await userResponse.Content.ReadFromJsonAsync<UserProfileDto>(cancellationToken: ct);
        if (userProfile is null)
        {
            UpdateSaga("Failed", error: "User profile payload invalid");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status502BadGateway));
        }

        var cartResponse = await cartClient.GetAsync($"/carts/{request.UserId}", ct);
        if (cartResponse.IsSuccessStatusCode)
        {
            var cart = await cartResponse.Content.ReadFromJsonAsync<CartDto>(cancellationToken: ct);
            if (cart is { Items.Count: > 0 } && !CartMatchesCheckout(cart.Items, request.Items))
            {
                UpdateSaga("Failed", error: "Cart mismatch");
                return await FailIdempotentAsync(Results.BadRequest(new { error = "Checkout line items must match the saved cart." }));
            }
        }

        try
        {
            UpdateSaga("CheckInventory");
            foreach (var line in request.Items)
            {
                var check = await InventoryGrpcResilience.ExecuteInventoryCallAsync(
                    inventoryPolicy,
                    inventoryGrpcSettings,
                    (deadline, cancellationToken) => inventoryClient.CheckAsync(new InventoryOperationRequest
                    {
                        ProductId = line.ProductId.ToString(),
                        Quantity = line.Quantity
                    }, deadline: deadline, cancellationToken: cancellationToken).ResponseAsync,
                    ct);
                if (!check.Success)
                {
                    UpdateSaga("Failed", error: check.Error);
                    return await FailIdempotentAsync(Results.BadRequest(new { error = check.Error }));
                }
            }
        }
        catch (BrokenCircuitException)
        {
            UpdateSaga("Failed", error: "Inventory circuit open");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
        catch (Exception ex) when (ex is RpcException or HttpRequestException)
        {
            logger.LogWarning(ex, "Inventory check step failed.");
            UpdateSaga("Failed", error: "Inventory check unavailable");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status502BadGateway));
        }

        var createOrderRequest = new CreateOrderRequest(request.UserId, request.Currency, request.Items);
        var orderResponse = await orderClient.PostAsJsonAsync("/orders", createOrderRequest, ct);
        if (!orderResponse.IsSuccessStatusCode)
        {
            UpdateSaga("Failed", error: "Order creation failed");
            return await FailIdempotentAsync(Results.StatusCode((int)orderResponse.StatusCode));
        }

        var order = await orderResponse.Content.ReadFromJsonAsync<OrderDto>(cancellationToken: ct);
        if (order is null)
        {
            UpdateSaga("Failed", error: "Order payload invalid");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status502BadGateway));
        }

        UpdateSaga("ReserveInventory", order.OrderId);
        var reserved = new List<CartItemDto>();
        try
        {
            foreach (var line in request.Items)
            {
                var reserve = await InventoryGrpcResilience.ExecuteInventoryCallAsync(
                    inventoryPolicy,
                    inventoryGrpcSettings,
                    (deadline, cancellationToken) => inventoryClient.ReserveAsync(new InventoryOperationRequest
                    {
                        ProductId = line.ProductId.ToString(),
                        Quantity = line.Quantity
                    }, deadline: deadline, cancellationToken: cancellationToken).ResponseAsync,
                    ct);

                if (!reserve.Success)
                {
                    await CompensateReleaseInventory(inventoryClient, inventoryPolicy, inventoryGrpcSettings, reserved, ct);
                    await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
                    UpdateSaga("Compensated", order.OrderId, reserve.Error);
                    return await FailIdempotentAsync(Results.BadRequest(new { error = reserve.Error }));
                }

                reserved.Add(line);
            }
        }
        catch (BrokenCircuitException)
        {
            await CompensateReleaseInventory(inventoryClient, inventoryPolicy, inventoryGrpcSettings, reserved, ct);
            await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
            UpdateSaga("Compensated", order.OrderId, "Inventory circuit open");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        }
        catch (Exception ex) when (ex is RpcException or HttpRequestException)
        {
            logger.LogWarning(ex, "Inventory reserve step failed.");
            await CompensateReleaseInventory(inventoryClient, inventoryPolicy, inventoryGrpcSettings, reserved, ct);
            await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
            UpdateSaga("Compensated", order.OrderId, "Inventory reserve unavailable");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status502BadGateway));
        }

        UpdateSaga("ProcessPayment", order.OrderId);
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
            await CompensateReleaseInventory(inventoryClient, inventoryPolicy, inventoryGrpcSettings, reserved, ct);
            await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
            UpdateSaga("Compensated", order.OrderId, "Payment failed");
            return await FailIdempotentAsync(Results.StatusCode((int)paymentResponse.StatusCode));
        }

        var payment = await paymentResponse.Content.ReadFromJsonAsync<PaymentDto>(cancellationToken: ct);
        if (payment is null)
        {
            await CompensateReleaseInventory(inventoryClient, inventoryPolicy, inventoryGrpcSettings, reserved, ct);
            await orderClient.PostAsync($"/orders/{order.OrderId}/fail", content: null, ct);
            UpdateSaga("Compensated", order.OrderId, "Payment payload invalid");
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status502BadGateway));
        }

        UpdateSaga("ConfirmOrder", order.OrderId);
        await orderClient.PostAsync($"/orders/{order.OrderId}/complete", content: null, ct);
        foreach (var line in reserved)
        {
            await InventoryGrpcResilience.ExecuteInventoryCallAsync(
                inventoryPolicy,
                inventoryGrpcSettings,
                (deadline, cancellationToken) => inventoryClient.CommitAsync(new InventoryOperationRequest
                {
                    ProductId = line.ProductId.ToString(),
                    Quantity = line.Quantity
                }, deadline: deadline, cancellationToken: cancellationToken).ResponseAsync,
                ct);
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

        var emailEvent = new EmailNotificationRequestedEvent(
            NotificationId: Guid.NewGuid(),
            OrderId: order.OrderId,
            UserId: request.UserId,
            CustomerEmail: userProfile.Email,
            CustomerName: userProfile.FullName,
            Amount: order.TotalAmount,
            Currency: order.Currency,
            RequestedAt: DateTimeOffset.UtcNow);

        try
        {
            await emailPublisher.PublishAsync(emailEvent, ct);
            logger.LogInformation(
                "Queued order confirmation email event {NotificationId} for order {OrderId}",
                emailEvent.NotificationId,
                order.OrderId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish email notification event for order {OrderId}", order.OrderId);
        }

        UpdateSaga("Completed", order.OrderId);
        return await CompleteIdempotentAsync(StatusCodes.Status200OK, new
        {
            sagaId,
            order,
            payment,
            shipment,
            status = "CheckoutCompleted"
        });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Checkout saga failed unexpectedly for user {UserId}", request.UserId);
            return await FailIdempotentAsync(Results.StatusCode(StatusCodes.Status500InternalServerError));
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

static async Task CompensateReleaseInventory(
    InventoryGrpc.InventoryGrpcClient inventoryClient,
    IAsyncPolicy<InventoryOperationReply> inventoryPolicy,
    InventoryGrpcCallSettings inventoryGrpcSettings,
    IReadOnlyList<CartItemDto> reservedItems,
    CancellationToken cancellationToken)
{
    foreach (var line in reservedItems)
    {
        await InventoryGrpcResilience.ExecuteInventoryCallAsync(
            inventoryPolicy,
            inventoryGrpcSettings,
            (deadline, token) => inventoryClient.ReleaseAsync(new InventoryOperationRequest
            {
                ProductId = line.ProductId.ToString(),
                Quantity = line.Quantity
            }, deadline: deadline, cancellationToken: token).ResponseAsync,
            cancellationToken);
    }
}

static bool TryGetAuthorizedUserId(HttpContext context, IConfiguration configuration, out Guid userId)
{
    userId = Guid.Empty;
    if (!context.Request.Headers.TryGetValue("Authorization", out var authorization))
    {
        return false;
    }

    var headerValue = authorization.ToString().Trim();
    if (!headerValue.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var token = headerValue["Bearer ".Length..].Trim();
    if (string.IsNullOrWhiteSpace(token))
    {
        return false;
    }

    var jwtIssuer = configuration["Auth:JwtIssuer"] ?? "online-store";
    var jwtAudience = configuration["Auth:JwtAudience"] ?? "online-store-clients";
    var signingKey = configuration["Auth:JwtSigningKey"] ?? "dev-super-secret-signing-key-min-32-chars";

    var tokenHandler = new JwtSecurityTokenHandler();
    var validationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtIssuer,
        ValidateAudience = true,
        ValidAudience = jwtAudience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };

    try
    {
        var principal = tokenHandler.ValidateToken(token, validationParameters, out _);
        var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return Guid.TryParse(userIdClaim, out userId);
    }
    catch
    {
        return false;
    }
}

static string ComputeCheckoutPayloadHash(CheckoutRequest request)
{
    var payload = JsonSerializer.Serialize(request);
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}

internal sealed class CheckoutSagaStore
{
    public ConcurrentDictionary<Guid, CheckoutSagaState> States { get; } = new();
}

internal sealed record CheckoutSagaState(
    Guid SagaId,
    Guid UserId,
    Guid? OrderId,
    string Step,
    DateTimeOffset UpdatedAt,
    string? ErrorMessage);

internal sealed record InventoryGrpcCallSettings(TimeSpan Timeout);

internal static class InventoryGrpcResilience
{
    public static Task<InventoryOperationReply> ExecuteInventoryCallAsync(
        IAsyncPolicy<InventoryOperationReply> inventoryPolicy,
        InventoryGrpcCallSettings callSettings,
        Func<DateTime, CancellationToken, Task<InventoryOperationReply>> operation,
        CancellationToken cancellationToken) =>
        inventoryPolicy.ExecuteAsync(
            async (_, token) =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(callSettings.Timeout);
                var deadline = DateTime.UtcNow.Add(callSettings.Timeout);
                return await operation(deadline, timeoutCts.Token);
            },
            new Context(),
            cancellationToken);

    public static IAsyncPolicy<InventoryOperationReply> BuildInventoryGrpcPolicy(Microsoft.Extensions.Logging.ILogger logger)
    {
        var jitterer = new Random();

        var retryPolicy = Policy<InventoryOperationReply>
            .Handle<RpcException>(IsTransientRpcException)
            .Or<HttpRequestException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromMilliseconds(150 * retryAttempt + jitterer.Next(0, 100)),
                onRetry: (outcome, delay, retryCount, _) =>
                {
                    logger.LogWarning(
                        outcome.Exception,
                        "Retrying inventory gRPC call in {DelayMs} ms (attempt {RetryAttempt})",
                        delay.TotalMilliseconds,
                        retryCount);
                });

        var circuitBreakerPolicy = Policy<InventoryOperationReply>
            .Handle<RpcException>(IsTransientRpcException)
            .Or<HttpRequestException>()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(30),
                onBreak: (outcome, breakDelay) =>
                    logger.LogWarning(
                        outcome.Exception,
                        "Inventory gRPC circuit opened for {BreakSeconds} seconds",
                        breakDelay.TotalSeconds),
                onReset: () => logger.LogInformation("Inventory gRPC circuit reset"),
                onHalfOpen: () => logger.LogInformation("Inventory gRPC circuit half-open"));

        return Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
    }

    private static bool IsTransientRpcException(RpcException rpcException) =>
        rpcException.StatusCode is
            Grpc.Core.StatusCode.Unavailable or
            Grpc.Core.StatusCode.DeadlineExceeded or
            Grpc.Core.StatusCode.Internal or
            Grpc.Core.StatusCode.ResourceExhausted;
}

internal sealed class EmailNotificationPublisher(IConfiguration configuration, ILogger<EmailNotificationPublisher> logger) : IDisposable
{
    private readonly string _topic = configuration["Messaging:Kafka:Topic"] ?? "email-notifications";
    private readonly IProducer<Null, string> _producer = new ProducerBuilder<Null, string>(new ProducerConfig
    {
        BootstrapServers = configuration["Messaging:Kafka:BootstrapServers"] ?? "localhost:9092",
        Acks = Acks.All
    }).Build();

    public async Task PublishAsync(EmailNotificationRequestedEvent notification, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(notification);
        var result = await _producer.ProduceAsync(
            _topic,
            new Message<Null, string> { Value = payload },
            cancellationToken);

        logger.LogInformation(
            "Email event for order {OrderId} published to {Topic}@{Partition}/{Offset}",
            notification.OrderId,
            _topic,
            result.Partition.Value,
            result.Offset.Value);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(3));
        _producer.Dispose();
    }
}
