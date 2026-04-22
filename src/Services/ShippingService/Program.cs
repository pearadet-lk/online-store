using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;

const string ServiceName = "shipping-service";
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
builder.Services.AddSingleton<ShipmentStore>();

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

app.MapGet("/health", () => Results.Ok(new { service = "shipping-service", status = "ok" }));
app.MapMetrics("/metrics");

app.MapPost("/shipments/{orderId:guid}", (Guid orderId, ShipmentStore store) =>
{
    var shipment = new ShipmentDto(
        Guid.NewGuid(),
        orderId,
        "DHL",
        $"TRK-{Guid.NewGuid():N}"[..16],
        "Dispatched",
        DateTimeOffset.UtcNow);
    store.ShipmentsByOrderId[orderId] = shipment;
    return Results.Created($"/shipments/{orderId}", shipment);
});

app.MapGet("/shipments/{orderId:guid}", (Guid orderId, ShipmentStore store) =>
{
    return store.ShipmentsByOrderId.TryGetValue(orderId, out var shipment)
        ? Results.Ok(shipment)
        : Results.NotFound();
});

app.Run();

internal sealed class ShipmentStore
{
    public ConcurrentDictionary<Guid, ShipmentDto> ShipmentsByOrderId { get; } = new();
}
