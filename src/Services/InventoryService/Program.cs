using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using Contracts.Grpc;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;

const string ServiceName = "inventory-service";
const string DefaultApiVersion = "v1";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, listenOptions => listenOptions.Protocols = HttpProtocols.Http1AndHttp2);
});
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
builder.Services.AddGrpc();
builder.Services.AddSingleton<InventoryStore>();

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

app.MapGet("/health", () => Results.Ok(new { service = "inventory-service", status = "ok" }));
app.MapMetrics("/metrics");
app.MapGrpcService<InventoryGrpcService>();

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
    var (success, error) = InventoryOperations.TryReserve(store, productId, quantity);
    if (!success)
    {
        return error == "not_found"
            ? Results.NotFound()
            : Results.BadRequest(new { error = "Insufficient stock." });
    }
    return Results.Ok(store.Items[productId]);
});

app.MapPost("/inventory/{productId:guid}/release", (Guid productId, int quantity, InventoryStore store) =>
{
    var (success, error) = InventoryOperations.TryRelease(store, productId, quantity);
    if (!success)
    {
        return error == "not_found"
            ? Results.NotFound()
            : Results.BadRequest(new { error = "Cannot release more than reserved quantity." });
    }
    return Results.Ok(store.Items[productId]);
});

app.MapPost("/inventory/{productId:guid}/commit", (Guid productId, int quantity, InventoryStore store) =>
{
    var (success, error) = InventoryOperations.TryCommit(store, productId, quantity);
    if (!success)
    {
        return error == "not_found"
            ? Results.NotFound()
            : Results.BadRequest(new { error = "Cannot commit more than reserved quantity." });
    }
    return Results.Ok(store.Items[productId]);
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

internal sealed class InventoryGrpcService(InventoryStore store) : InventoryGrpc.InventoryGrpcBase
{
    public override Task<InventoryOperationReply> Check(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryCheck(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Insufficient stock."));
    }

    public override Task<InventoryOperationReply> Reserve(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryReserve(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Insufficient stock."));
    }

    public override Task<InventoryOperationReply> Release(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryRelease(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Cannot release more than reserved quantity."));
    }

    public override Task<InventoryOperationReply> Commit(InventoryOperationRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.ProductId, out var productId))
        {
            return Task.FromResult(new InventoryOperationReply { Success = false, StatusCode = 400, Error = "Invalid product id." });
        }

        var (success, error) = InventoryOperations.TryCommit(store, productId, request.Quantity);
        return Task.FromResult(ToReply(success, error, "Cannot commit more than reserved quantity."));
    }

    private static InventoryOperationReply ToReply(bool success, string? errorCode, string validationError) =>
        success
            ? new InventoryOperationReply { Success = true, StatusCode = 200 }
            : errorCode switch
            {
                "not_found" => new InventoryOperationReply { Success = false, StatusCode = 404, Error = "Product not found." },
                _ => new InventoryOperationReply { Success = false, StatusCode = 400, Error = validationError }
            };
}

internal static class InventoryOperations
{
    public static (bool Success, string? ErrorCode) TryCheck(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        return item.AvailableQty >= quantity
            ? (true, null)
            : (false, "invalid");
    }

    public static (bool Success, string? ErrorCode) TryReserve(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        if (item.AvailableQty < quantity)
        {
            return (false, "invalid");
        }

        store.Items[productId] = item with
        {
            AvailableQty = item.AvailableQty - quantity,
            ReservedQty = item.ReservedQty + quantity,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (true, null);
    }

    public static (bool Success, string? ErrorCode) TryRelease(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        if (item.ReservedQty < quantity)
        {
            return (false, "invalid");
        }

        store.Items[productId] = item with
        {
            AvailableQty = item.AvailableQty + quantity,
            ReservedQty = item.ReservedQty - quantity,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (true, null);
    }

    public static (bool Success, string? ErrorCode) TryCommit(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        if (item.ReservedQty < quantity)
        {
            return (false, "invalid");
        }

        store.Items[productId] = item with
        {
            ReservedQty = item.ReservedQty - quantity,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (true, null);
    }
}
