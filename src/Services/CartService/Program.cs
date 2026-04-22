using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;

const string ServiceName = "cart-service";
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
builder.Services.AddSingleton<CartStore>();

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

app.MapGet("/health", () => Results.Ok(new { service = "cart-service", status = "ok", recommendation = "Use Redis in production" }));
app.MapMetrics("/metrics");

app.MapGet("/carts/{userId:guid}", (Guid userId, CartStore store) =>
{
    if (!store.CartByUserId.TryGetValue(userId, out var cart))
    {
        return Results.Ok(new CartDto(Guid.NewGuid(), userId, Array.Empty<CartItemDto>(), DateTimeOffset.UtcNow));
    }

    return Results.Ok(cart);
});

app.MapPut("/carts/{userId:guid}", (Guid userId, UpsertCartRequest request, CartStore store) =>
{
    if (userId != request.UserId)
    {
        return Results.BadRequest(new { error = "Route userId must match request userId." });
    }

    var existingCartId = store.CartByUserId.TryGetValue(userId, out var existing)
        ? existing.CartId
        : Guid.NewGuid();

    var cart = new CartDto(existingCartId, userId, request.Items, DateTimeOffset.UtcNow);
    store.CartByUserId[userId] = cart;
    return Results.Ok(cart);
});

app.MapDelete("/carts/{userId:guid}", (Guid userId, CartStore store) =>
{
    store.CartByUserId.TryRemove(userId, out _);
    return Results.NoContent();
});

app.Run();

internal sealed class CartStore
{
    public ConcurrentDictionary<Guid, CartDto> CartByUserId { get; } = new();
}
