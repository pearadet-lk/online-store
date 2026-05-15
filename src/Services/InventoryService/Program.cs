using System.Diagnostics;
using Contracts;
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
        .AddOnlineStoreTraceExporters(builder.Configuration));
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
app.UseDefaultApiVersioning(DefaultApiVersion);
app.UseRouting();

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
