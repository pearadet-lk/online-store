using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;

const string ServiceName = "email-service";
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
            .AddOnlineStoreTraceExporters(builder.Configuration));

    builder.Services.AddOpenApi();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();
    builder.Services.AddSingleton<EmailStatusStore>();
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
    builder.Services.AddHostedService<EmailNotificationConsumer>();

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

    app.MapGet("/health", (IConfiguration configuration) => Results.Ok(new
    {
        service = ServiceName,
        status = "ok",
        topic = configuration["Messaging:Kafka:Topic"] ?? "email-notifications"
    }));

    app.MapGet("/email/status/{orderId:guid}", (Guid orderId, EmailStatusStore store) =>
    {
        return store.StatusByOrderId.TryGetValue(orderId, out var status)
            ? Results.Ok(status)
            : Results.NotFound(new { error = "No email status found for order." });
    });

    app.MapGet("/email/status", (EmailStatusStore store) =>
        Results.Ok(store.StatusByOrderId.Values.OrderByDescending(x => x.UpdatedAt)));

    app.MapMetrics("/metrics");
    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Email service host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

internal interface IEmailSender
{
    Task SendOrderConfirmation(EmailNotificationRequestedEvent notification, CancellationToken cancellationToken);
}
