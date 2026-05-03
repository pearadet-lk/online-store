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

const string ServiceName = "history-service";
const string DefaultApiVersion = "v1";
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
builder.Services.AddSingleton<HistoryStore>();

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

app.MapGet("/health", () => Results.Ok(new { service = "history-service", status = "ok" }));
app.MapMetrics("/metrics");

app.MapPost("/history/events", (OrderHistoryEventDto request, HistoryStore store) =>
{
    var history = request with
    {
        HistoryId = request.HistoryId == Guid.Empty ? Guid.NewGuid() : request.HistoryId,
        CreatedAt = request.CreatedAt == default ? DateTimeOffset.UtcNow : request.CreatedAt
    };

    store.EventsByUserId.AddOrUpdate(
        history.UserId,
        _ => new List<OrderHistoryEventDto> { history },
        (_, existing) =>
        {
            existing.Add(history);
            return existing;
        });

    return Results.Created($"/history/users/{history.UserId}", history);
});

app.MapGet("/history/users/{userId:guid}", (Guid userId, HistoryStore store) =>
{
    return store.EventsByUserId.TryGetValue(userId, out var events)
        ? Results.Ok(events.OrderByDescending(x => x.CreatedAt))
        : Results.Ok(Array.Empty<OrderHistoryEventDto>());
});

app.Run();

internal sealed class HistoryStore
{
    public ConcurrentDictionary<Guid, List<OrderHistoryEventDto>> EventsByUserId { get; } = new();
}
