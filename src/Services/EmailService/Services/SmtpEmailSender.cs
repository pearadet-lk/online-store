using System.Net;
using System.Net.Mail;
using Contracts;

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
