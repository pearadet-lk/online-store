using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace PaymentService.Tests;

public sealed class PaymentServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("PAYMENT_SERVICE_URL", "http://localhost:5031"));

    [Fact]
    public async Task Authorize_WithSameIdempotencyKeyAndPayload_ReturnsSamePayment()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var paymentUrl = LiveTestSettings.GetServiceUrl("PAYMENT_SERVICE_URL", "http://localhost:5031");
        using var client = new HttpClient { BaseAddress = new Uri(paymentUrl) };
        var idempotencyKey = $"payment-test-{Guid.NewGuid():N}";

        using var first = await SendAuthorizeAsync(client, idempotencyKey, orderId: Guid.NewGuid(), amount: 100m, currency: "USD");
        first.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var paymentId1 = firstJson.RootElement.GetProperty("paymentId").GetGuid();

        var orderId = firstJson.RootElement.GetProperty("orderId").GetGuid();
        using var second = await SendAuthorizeAsync(client, idempotencyKey, orderId, amount: 100m, currency: "USD");
        second.EnsureSuccessStatusCode();
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var paymentId2 = secondJson.RootElement.GetProperty("paymentId").GetGuid();

        Assert.Equal(paymentId1, paymentId2);
    }

    [Fact]
    public async Task Authorize_WithSameIdempotencyKeyAndDifferentPayload_ReturnsConflict()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var paymentUrl = LiveTestSettings.GetServiceUrl("PAYMENT_SERVICE_URL", "http://localhost:5031");
        using var client = new HttpClient { BaseAddress = new Uri(paymentUrl) };
        var idempotencyKey = $"payment-test-{Guid.NewGuid():N}";
        var orderId = Guid.NewGuid();

        using var first = await SendAuthorizeAsync(client, idempotencyKey, orderId, amount: 100m, currency: "USD");
        first.EnsureSuccessStatusCode();

        using var conflict = await SendAuthorizeAsync(client, idempotencyKey, orderId, amount: 200m, currency: "USD");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Authorize_MissingIdempotencyKey_ReturnsBadRequest()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var paymentUrl = LiveTestSettings.GetServiceUrl("PAYMENT_SERVICE_URL", "http://localhost:5031");
        using var client = new HttpClient { BaseAddress = new Uri(paymentUrl) };
        using var response = await client.PostAsJsonAsync("/payments/authorize", new
        {
            orderId = Guid.NewGuid(),
            amount = 99m,
            currency = "USD",
            paymentMethodToken = "pm_card_visa"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAuthorizeAsync(HttpClient client, string idempotencyKey, Guid orderId, decimal amount, string currency)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/payments/authorize")
        {
            Content = JsonContent.Create(new
            {
                orderId,
                amount,
                currency,
                paymentMethodToken = "pm_card_visa"
            })
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(request);
    }
}
