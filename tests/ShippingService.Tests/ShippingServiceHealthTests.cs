using System.Net;
using System.Text.Json;
using TestInfrastructure;

namespace ShippingService.Tests;

public sealed class ShippingServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("SHIPPING_SERVICE_URL", "http://localhost:5219"));

    [Fact]
    public async Task CreateShipment_ThenGet_ReturnsPersistedShipment()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("SHIPPING_SERVICE_URL", "http://localhost:5219");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var orderId = Guid.NewGuid();

        using var create = await client.PostAsync($"/shipments/{orderId}", content: null);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var createJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(orderId, createJson.RootElement.GetProperty("orderId").GetGuid());
        Assert.Equal("Dispatched", createJson.RootElement.GetProperty("status").GetString());

        using var get = await client.GetAsync($"/shipments/{orderId}");
        get.EnsureSuccessStatusCode();
        using var getJson = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(orderId, getJson.RootElement.GetProperty("orderId").GetGuid());
        Assert.Equal("Dispatched", getJson.RootElement.GetProperty("status").GetString());
    }
}
