using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;

const string ServiceName = "inventory-service";
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
builder.Services.AddSingleton<InventoryStore>();

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

app.MapGet("/health", () => Results.Ok(new { service = "inventory-service", status = "ok" }));
app.MapMetrics("/metrics");

app.MapGet("/inventory/{productId:guid}", (Guid productId, InventoryStore store) =>
{
    return store.Items.TryGetValue(productId, out var item)
        ? Results.Ok(item)
        : Results.NotFound();
});

app.MapPut("/inventory/{productId:guid}", (Guid productId, int availableQty, InventoryStore store) =>
{
    var existingReservedQty = store.Items.TryGetValue(productId, out var existing) ? existing.ReservedQty : 0;
    var updated = new InventoryItemDto(productId, Math.Max(0, availableQty), existingReservedQty, DateTimeOffset.UtcNow);
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.MapPost("/inventory/{productId:guid}/reserve", (Guid productId, int quantity, InventoryStore store) =>
{
    if (!store.Items.TryGetValue(productId, out var item))
    {
        return Results.NotFound();
    }

    if (quantity <= 0 || item.AvailableQty < quantity)
    {
        return Results.BadRequest(new { error = "Insufficient stock." });
    }

    var updated = item with
    {
        AvailableQty = item.AvailableQty - quantity,
        ReservedQty = item.ReservedQty + quantity,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.MapPost("/inventory/{productId:guid}/release", (Guid productId, int quantity, InventoryStore store) =>
{
    if (quantity <= 0)
    {
        return Results.BadRequest(new { error = "Quantity must be positive." });
    }

    if (!store.Items.TryGetValue(productId, out var item))
    {
        return Results.NotFound();
    }

    if (item.ReservedQty < quantity)
    {
        return Results.BadRequest(new { error = "Cannot release more than reserved quantity." });
    }

    var updated = item with
    {
        AvailableQty = item.AvailableQty + quantity,
        ReservedQty = item.ReservedQty - quantity,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.MapPost("/inventory/{productId:guid}/commit", (Guid productId, int quantity, InventoryStore store) =>
{
    if (quantity <= 0)
    {
        return Results.BadRequest(new { error = "Quantity must be positive." });
    }

    if (!store.Items.TryGetValue(productId, out var item))
    {
        return Results.NotFound();
    }

    if (item.ReservedQty < quantity)
    {
        return Results.BadRequest(new { error = "Cannot commit more than reserved quantity." });
    }

    var updated = item with
    {
        ReservedQty = item.ReservedQty - quantity,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.Run();

internal sealed class InventoryStore
{
    public ConcurrentDictionary<Guid, InventoryItemDto> Items { get; } = new(
        new[]
        {
            new KeyValuePair<Guid, InventoryItemDto>(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                new InventoryItemDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), 100, 0, DateTimeOffset.UtcNow)),
            new KeyValuePair<Guid, InventoryItemDto>(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                new InventoryItemDto(Guid.Parse("22222222-2222-2222-2222-222222222222"), 100, 0, DateTimeOffset.UtcNow))
        });
}
