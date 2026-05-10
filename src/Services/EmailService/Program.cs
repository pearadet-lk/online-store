using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using Confluent.Kafka;
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

internal sealed class EmailNotificationConsumer(
    IConfiguration configuration,
    ILogger<EmailNotificationConsumer> logger,
    EmailStatusStore store,
    IEmailSender emailSender) : BackgroundService
{
    private readonly string _topic = configuration["Messaging:Kafka:Topic"] ?? "email-notifications";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(() => ConsumeLoop(stoppingToken), stoppingToken);
    }

    private void ConsumeLoop(CancellationToken stoppingToken)
    {
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = configuration["Messaging:Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = configuration["Messaging:Kafka:GroupId"] ?? "email-service",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };

        using var consumer = new ConsumerBuilder<Ignore, string>(consumerConfig).Build();
        consumer.Subscribe(_topic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<Ignore, string>? result = null;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (result is null || string.IsNullOrWhiteSpace(result.Message.Value))
                {
                    continue;
                }

                EmailNotificationRequestedEvent? notification;
                try
                {
                    notification = JsonSerializer.Deserialize<EmailNotificationRequestedEvent>(result.Message.Value);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Skipping malformed email notification payload.");
                    consumer.Commit(result);
                    continue;
                }

                if (notification is null)
                {
                    consumer.Commit(result);
                    continue;
                }

                var existing = store.StatusByOrderId.TryGetValue(notification.OrderId, out var current)
                    ? current
                    : new EmailSendStatusDto(
                        notification.NotificationId,
                        notification.OrderId,
                        notification.CustomerEmail,
                        "Queued",
                        0,
                        null,
                        DateTimeOffset.UtcNow);

                var processing = existing with
                {
                    Status = "Processing",
                    AttemptCount = existing.AttemptCount + 1,
                    ErrorMessage = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                store.StatusByOrderId[notification.OrderId] = processing;

                try
                {
                    emailSender.SendOrderConfirmation(notification, stoppingToken).GetAwaiter().GetResult();

                    store.StatusByOrderId[notification.OrderId] = processing with
                    {
                        Status = "Sent",
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    logger.LogInformation(
                        "Order confirmation email sent for order {OrderId} to {Email}",
                        notification.OrderId,
                        notification.CustomerEmail);
                }
                catch (Exception ex)
                {
                    store.StatusByOrderId[notification.OrderId] = processing with
                    {
                        Status = "Failed",
                        ErrorMessage = ex.Message,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    logger.LogError(
                        ex,
                        "Failed sending email for order {OrderId} to {Email}",
                        notification.OrderId,
                        notification.CustomerEmail);
                }

                consumer.Commit(result);
            }
        }
        finally
        {
            consumer.Close();
        }
    }
}

internal interface IEmailSender
{
    Task SendOrderConfirmation(EmailNotificationRequestedEvent notification, CancellationToken cancellationToken);
}

internal sealed class SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendOrderConfirmation(EmailNotificationRequestedEvent notification, CancellationToken cancellationToken)
    {
        var smtpHost = configuration["Email:SmtpHost"];
        var smtpPort = int.TryParse(configuration["Email:SmtpPort"], out var parsedPort) ? parsedPort : 25;
        var fromAddress = configuration["Email:FromAddress"] ?? "noreply@onlinestore.local";

        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            await Task.Delay(150, cancellationToken);
            logger.LogInformation(
                "SMTP not configured; mock-sent order confirmation email for {OrderId} to {Email}",
                notification.OrderId,
                notification.CustomerEmail);
            return;
        }

        using var client = new SmtpClient(smtpHost, smtpPort)
        {
            EnableSsl = bool.TryParse(configuration["Email:EnableSsl"], out var ssl) && ssl
        };

        var smtpUser = configuration["Email:SmtpUser"];
        var smtpPassword = configuration["Email:SmtpPassword"];
        if (!string.IsNullOrWhiteSpace(smtpUser))
        {
            client.Credentials = new NetworkCredential(smtpUser, smtpPassword);
        }

        using var message = new MailMessage(
            fromAddress,
            notification.CustomerEmail,
            $"Order Confirmation #{notification.OrderId}",
            $"Hello {notification.CustomerName}, your order {notification.OrderId} for {notification.Amount} {notification.Currency} has been placed successfully.");

        await client.SendMailAsync(message, cancellationToken);
    }
}

internal sealed class EmailStatusStore
{
    public ConcurrentDictionary<Guid, EmailSendStatusDto> StatusByOrderId { get; } = new();
}
