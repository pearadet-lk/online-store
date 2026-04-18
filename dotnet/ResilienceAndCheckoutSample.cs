using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;
using Stripe;

namespace OnlineStore.Blueprint;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddResilientHttpClients(this IServiceCollection services, IConfiguration config)
    {
        var jitterer = new Random();

        static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(Random random) =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .Or<TimeoutRejectedException>()
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: retryAttempt =>
                        TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))
                        + TimeSpan.FromMilliseconds(random.Next(0, 250)));

        static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy() =>
            HttpPolicyExtensions
                .HandleTransientHttpError()
                .Or<TimeoutRejectedException>()
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromSeconds(30));

        static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy() =>
            Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(5));

        services.AddHttpClient("InventoryService", client =>
            {
                client.BaseAddress = new Uri(config["Services:InventoryBaseUrl"]!);
            })
            .AddPolicyHandler(GetRetryPolicy(jitterer))
            .AddPolicyHandler(GetCircuitBreakerPolicy())
            .AddPolicyHandler(GetTimeoutPolicy());

        services.AddHttpClient("ShippingService", client =>
            {
                client.BaseAddress = new Uri(config["Services:ShippingBaseUrl"]!);
            })
            .AddPolicyHandler(GetRetryPolicy(jitterer))
            .AddPolicyHandler(GetCircuitBreakerPolicy())
            .AddPolicyHandler(GetTimeoutPolicy());

        return services;
    }

    public static IServiceCollection AddApiRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
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

        return services;
    }
}

public static class RouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/checkout", CheckoutHandler).RequireRateLimiting("checkout");
        app.MapGet("/products", ProductsHandler).RequireRateLimiting("catalog");
        return app;
    }

    private static IResult CheckoutHandler() => Results.Accepted();
    private static IResult ProductsHandler() => Results.Ok();
}

public sealed class PaymentService
{
    private readonly PaymentDbContext _db;
    private readonly PaymentIntentService _stripe;

    public PaymentService(PaymentDbContext db, PaymentIntentService stripe)
    {
        _db = db;
        _stripe = stripe;
    }

    public async Task<PaymentResultDto> ProcessPaymentAsync(
        ProcessPaymentRequest request,
        string idempotencyKey,
        CancellationToken ct)
    {
        var normalizedRequest = JsonSerializer.Serialize(request);
        var requestHash = ComputeSha256(normalizedRequest);

        var existing = await _db.IdempotencyRequests
            .SingleOrDefaultAsync(x => x.RequestKey == idempotencyKey, ct);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Idempotency key already used with different payload.");
            }

            return JsonSerializer.Deserialize<PaymentResultDto>(existing.ResponseJson)
                ?? throw new InvalidOperationException("Stored idempotent response was invalid.");
        }

        var intent = await _stripe.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)(request.Amount * 100),
            Currency = request.Currency.ToLowerInvariant(),
            PaymentMethod = request.PaymentMethodId,
            Confirm = true
        }, new RequestOptions
        {
            IdempotencyKey = idempotencyKey
        }, ct);

        var result = new PaymentResultDto(
            request.OrderId,
            intent.Id,
            intent.Status,
            request.Amount,
            request.Currency);

        _db.IdempotencyRequests.Add(new PaymentIdempotencyRequest
        {
            RequestKey = idempotencyKey,
            RequestHash = requestHash,
            ResponseStatusCode = 200,
            ResponseJson = JsonSerializer.Serialize(result),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24)
        });

        await _db.SaveChangesAsync(ct);
        return result;
    }

    private static string ComputeSha256(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }
}

public sealed class CheckoutSagaOrchestrator
{
    private readonly IOrderClient _orderClient;
    private readonly IInventoryClient _inventoryClient;
    private readonly IPaymentClient _paymentClient;
    private readonly IShippingClient _shippingClient;

    public CheckoutSagaOrchestrator(
        IOrderClient orderClient,
        IInventoryClient inventoryClient,
        IPaymentClient paymentClient,
        IShippingClient shippingClient)
    {
        _orderClient = orderClient;
        _inventoryClient = inventoryClient;
        _paymentClient = paymentClient;
        _shippingClient = shippingClient;
    }

    public async Task ExecuteAsync(CheckoutCommand command, CancellationToken ct)
    {
        await _orderClient.CreatePendingOrder(command, ct);

        try
        {
            await _inventoryClient.ReserveStock(command.OrderId, command.Items, ct);
            await _paymentClient.AuthorizePayment(command.OrderId, command.Amount, command.Currency, command.IdempotencyKey, ct);
            await _orderClient.MarkPaid(command.OrderId, ct);
            await _shippingClient.CreateShipment(command.OrderId, ct);
            await _orderClient.MarkCompleted(command.OrderId, ct);
        }
        catch
        {
            await _paymentClient.VoidIfAuthorized(command.OrderId, ct);
            await _inventoryClient.ReleaseStock(command.OrderId, ct);
            await _orderClient.MarkFailed(command.OrderId, ct);
            throw;
        }
    }
}

public sealed record ProcessPaymentRequest(Guid OrderId, decimal Amount, string Currency, string PaymentMethodId);
public sealed record PaymentResultDto(Guid OrderId, string StripePaymentIntentId, string StripeStatus, decimal Amount, string Currency);
public sealed record CheckoutCommand(Guid OrderId, decimal Amount, string Currency, string IdempotencyKey, IReadOnlyList<CheckoutItem> Items);
public sealed record CheckoutItem(Guid ProductId, int Quantity);

public sealed class PaymentIdempotencyRequest
{
    public string RequestKey { get; set; } = default!;
    public string RequestHash { get; set; } = default!;
    public int ResponseStatusCode { get; set; }
    public string ResponseJson { get; set; } = default!;
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class PaymentDbContext : DbContext
{
    public DbSet<PaymentIdempotencyRequest> IdempotencyRequests => Set<PaymentIdempotencyRequest>();
}

public interface IOrderClient
{
    Task CreatePendingOrder(CheckoutCommand command, CancellationToken ct);
    Task MarkPaid(Guid orderId, CancellationToken ct);
    Task MarkCompleted(Guid orderId, CancellationToken ct);
    Task MarkFailed(Guid orderId, CancellationToken ct);
}

public interface IInventoryClient
{
    Task ReserveStock(Guid orderId, IReadOnlyList<CheckoutItem> items, CancellationToken ct);
    Task ReleaseStock(Guid orderId, CancellationToken ct);
}

public interface IPaymentClient
{
    Task AuthorizePayment(Guid orderId, decimal amount, string currency, string idempotencyKey, CancellationToken ct);
    Task VoidIfAuthorized(Guid orderId, CancellationToken ct);
}

public interface IShippingClient
{
    Task CreateShipment(Guid orderId, CancellationToken ct);
}
