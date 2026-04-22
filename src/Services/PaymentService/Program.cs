using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Stripe;

const string ServiceName = "payment-service";

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
    builder.Services.AddSingleton<PaymentStore>();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwagger();
        app.UseSwaggerUI();
    }

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

    app.MapGet("/health", (IConfiguration config) =>
    {
        var stripeConfigured = !string.IsNullOrWhiteSpace(config["Stripe:SecretKey"]);
        return Results.Ok(new { service = "payment-service", status = "ok", stripe = stripeConfigured ? "live" : "mock" });
    });
    app.MapMetrics("/metrics");

    app.MapPost("/payments/authorize", async (
        AuthorizePaymentRequest request,
        HttpContext context,
        PaymentStore store,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct) =>
    {
        var logger = loggerFactory.CreateLogger("PaymentAuthorize");
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyValues))
        {
            return Results.BadRequest(new { error = "Missing Idempotency-Key header." });
        }

        var idempotencyKey = idempotencyKeyValues.ToString().Trim();
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return Results.BadRequest(new { error = "Idempotency-Key header cannot be empty." });
        }

        var payload = JsonSerializer.Serialize(request);
        var payloadHash = ComputeSha256(payload);

        if (store.IdempotencyResponses.TryGetValue(idempotencyKey, out var existing))
        {
            if (!string.Equals(existing.RequestHash, payloadHash, StringComparison.Ordinal))
            {
                return Results.Conflict(new { error = "Idempotency key reused with different payload." });
            }

            return Results.Ok(existing.Payment);
        }

        var stripeSecret = config["Stripe:SecretKey"];
        PaymentDto payment;
        if (!string.IsNullOrWhiteSpace(stripeSecret))
        {
            StripeConfiguration.ApiKey = stripeSecret;
            var service = new PaymentIntentService();
            var options = new PaymentIntentCreateOptions
            {
                Amount = (long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero),
                Currency = request.Currency.ToLowerInvariant(),
                PaymentMethod = request.PaymentMethodToken,
                Confirm = true,
                OffSession = true,
                Metadata = new Dictionary<string, string> { ["order_id"] = request.OrderId.ToString() }
            };

            try
            {
                var intent = await service.CreateAsync(options, cancellationToken: ct);
                payment = new PaymentDto(
                    Guid.NewGuid(),
                    request.OrderId,
                    request.Amount,
                    request.Currency.ToUpperInvariant(),
                    MapStripeStatus(intent.Status),
                    intent.Id,
                    DateTimeOffset.UtcNow);
            }
            catch (StripeException ex)
            {
                logger.LogWarning(ex, "Stripe authorization failed for order {OrderId}", request.OrderId);
                return Results.BadRequest(new { error = "Stripe authorization failed.", detail = ex.StripeError?.Message });
            }
        }
        else
        {
            payment = new PaymentDto(
                Guid.NewGuid(),
                request.OrderId,
                request.Amount,
                request.Currency.ToUpperInvariant(),
                "Authorized",
                $"mock_stripe_pi_{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow);
        }

        store.PaymentsByOrderId[request.OrderId] = payment;
        store.IdempotencyResponses[idempotencyKey] = new IdempotencyEntry(payloadHash, payment);

        logger.LogInformation(
            "[EmailStub] Would send payment receipt email for order {OrderId} (provider ref {Ref})",
            request.OrderId,
            payment.ProviderReference);

        return Results.Ok(payment);
    });

    app.MapGet("/payments/{orderId:guid}", (Guid orderId, PaymentStore store) =>
    {
        return store.PaymentsByOrderId.TryGetValue(orderId, out var payment)
            ? Results.Ok(payment)
            : Results.NotFound();
    });

    app.MapPost("/payments/{orderId:guid}/void", (Guid orderId, PaymentStore store) =>
    {
        if (!store.PaymentsByOrderId.TryGetValue(orderId, out var payment))
        {
            return Results.NotFound();
        }

        store.PaymentsByOrderId[orderId] = payment with { Status = "Voided" };
        return Results.Ok(store.PaymentsByOrderId[orderId]);
    });

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Payment service host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static string MapStripeStatus(string status) =>
    status switch
    {
        "succeeded" => "Authorized",
        "requires_action" or "requires_confirmation" => "Pending",
        _ => status
    };

static string ComputeSha256(string input)
{
    var bytes = Encoding.UTF8.GetBytes(input);
    var hash = SHA256.HashData(bytes);
    return Convert.ToHexString(hash);
}

internal sealed class PaymentStore
{
    public ConcurrentDictionary<Guid, PaymentDto> PaymentsByOrderId { get; } = new();
    public ConcurrentDictionary<string, IdempotencyEntry> IdempotencyResponses { get; } = new();
}

internal sealed record IdempotencyEntry(string RequestHash, PaymentDto Payment);
