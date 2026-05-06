using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace OrderService.Tests;

public sealed class OrderServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("ORDER_SERVICE_URL", "http://localhost:5240"));

    [Fact]
    public async Task CreateOrder_ThenComplete_UpdatesStatusToCompleted()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var orderUrl = LiveTestSettings.GetServiceUrl("ORDER_SERVICE_URL", "http://localhost:5240");
        using var client = new HttpClient { BaseAddress = new Uri(orderUrl) };
        var orderId = await CreateOrderAsync(client);

        using var complete = await client.PostAsync($"/orders/{orderId}/complete", content: null);
        complete.EnsureSuccessStatusCode();
        using var completeJson = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        Assert.Equal("Completed", completeJson.RootElement.GetProperty("status").GetString());

        using var get = await client.GetAsync($"/orders/{orderId}");
        get.EnsureSuccessStatusCode();
        using var getJson = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("Completed", getJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateOrder_ThenFail_UpdatesStatusToFailed()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var orderUrl = LiveTestSettings.GetServiceUrl("ORDER_SERVICE_URL", "http://localhost:5240");
        using var client = new HttpClient { BaseAddress = new Uri(orderUrl) };
        var orderId = await CreateOrderAsync(client);

        using var fail = await client.PostAsync($"/orders/{orderId}/fail", content: null);
        fail.EnsureSuccessStatusCode();
        using var failJson = JsonDocument.Parse(await fail.Content.ReadAsStringAsync());
        Assert.Equal("Failed", failJson.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CompleteOrFail_NonExistingOrder_ReturnsNotFound()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var orderUrl = LiveTestSettings.GetServiceUrl("ORDER_SERVICE_URL", "http://localhost:5240");
        using var client = new HttpClient { BaseAddress = new Uri(orderUrl) };
        var missingOrderId = Guid.NewGuid();

        using var complete = await client.PostAsync($"/orders/{missingOrderId}/complete", content: null);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);

        using var fail = await client.PostAsync($"/orders/{missingOrderId}/fail", content: null);
        Assert.Equal(HttpStatusCode.NotFound, fail.StatusCode);
    }

    private static async Task<Guid> CreateOrderAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/orders", new
        {
            userId = Guid.NewGuid(),
            currency = "USD",
            items = new object[]
            {
                new { productId = Guid.NewGuid(), quantity = 2, unitPrice = 10m }
            }
        });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("orderId").GetGuid();
    }
}
