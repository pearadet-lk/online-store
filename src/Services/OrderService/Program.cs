using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;

const string ServiceName = "order-service";
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
    builder.Services.AddSingleton<OrderStore>();

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

    app.MapGet("/health", () => Results.Ok(new { service = "order-service", status = "ok" }));
    app.MapMetrics("/metrics");

    app.MapPost("/orders", (CreateOrderRequest request, OrderStore store) =>
    {
        var totalAmount = request.Items.Sum(x => x.Quantity * x.UnitPrice);
        var order = new OrderDto(
            Guid.NewGuid(),
            request.UserId,
            totalAmount,
            request.Currency.ToUpperInvariant(),
            "Pending",
            DateTimeOffset.UtcNow);

        store.Orders[order.OrderId] = new OrderEntry(order, request.Items);
        return Results.Created($"/orders/{order.OrderId}", order);
    });

    app.MapGet("/orders/{orderId:guid}", (Guid orderId, OrderStore store) =>
    {
        return store.Orders.TryGetValue(orderId, out var entry)
            ? Results.Ok(entry.Order)
            : Results.NotFound();
    });

    app.MapPost("/orders/{orderId:guid}/complete", (Guid orderId, OrderStore store) =>
    {
        if (!store.Orders.TryGetValue(orderId, out var entry))
        {
            return Results.NotFound();
        }

        var updatedOrder = entry.Order with { Status = "Completed" };
        store.Orders[orderId] = entry with { Order = updatedOrder };
        return Results.Ok(updatedOrder);
    });

    app.MapPost("/orders/{orderId:guid}/fail", (Guid orderId, OrderStore store) =>
    {
        if (!store.Orders.TryGetValue(orderId, out var entry))
        {
            return Results.NotFound();
        }

        var updatedOrder = entry.Order with { Status = "Failed" };
        store.Orders[orderId] = entry with { Order = updatedOrder };
        return Results.Ok(updatedOrder);
    });

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Order service host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

internal sealed class OrderStore
{
    public ConcurrentDictionary<Guid, OrderEntry> Orders { get; } = new();
}

internal sealed record OrderEntry(OrderDto Order, IReadOnlyList<CartItemDto> Items);
