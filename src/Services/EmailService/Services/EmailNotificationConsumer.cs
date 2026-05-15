using System.Text.Json;
using Confluent.Kafka;
using Contracts;

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
