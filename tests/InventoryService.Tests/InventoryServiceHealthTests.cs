using System.Net;
using System.Text.Json;
using TestInfrastructure;

namespace InventoryService.Tests;

public sealed class InventoryServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("INVENTORY_SERVICE_URL", "http://localhost:5212"));

    [Fact]
    public async Task ReserveRequests_InParallel_DoNotOverbookInventory()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var inventoryUrl = LiveTestSettings.GetServiceUrl("INVENTORY_SERVICE_URL", "http://localhost:5212");
        var productId = Guid.NewGuid();
        using var client = new HttpClient { BaseAddress = new Uri(inventoryUrl) };

        using var seedResponse = await client.PutAsync($"/inventory/{productId}?availableQty=30", content: null);
        seedResponse.EnsureSuccessStatusCode();

        var reserveTasks = Enumerable.Range(0, 10)
            .Select(_ => client.PostAsync($"/inventory/{productId}/reserve?quantity=5", content: null))
            .ToArray();

        await Task.WhenAll(reserveTasks);
        var successCount = reserveTasks.Count(x => x.Result.IsSuccessStatusCode);
        Assert.True(successCount <= 6, $"Expected at most 6 successful reservations, got {successCount}.");

        using var finalStateResponse = await client.GetAsync($"/inventory/{productId}");
        finalStateResponse.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await finalStateResponse.Content.ReadAsStringAsync());

        var availableQty = json.RootElement.GetProperty("availableQty").GetInt32();
        var reservedQty = json.RootElement.GetProperty("reservedQty").GetInt32();

        Assert.True(availableQty >= 0, $"availableQty should be >= 0 but was {availableQty}.");
        Assert.Equal(30, availableQty + reservedQty);
    }

    [Fact]
    public async Task ReserveReleaseCommit_Lifecycle_UpdatesInventoryConsistently()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var inventoryUrl = LiveTestSettings.GetServiceUrl("INVENTORY_SERVICE_URL", "http://localhost:5212");
        var productId = Guid.NewGuid();
        using var client = new HttpClient { BaseAddress = new Uri(inventoryUrl) };

        using var seed = await client.PutAsync($"/inventory/{productId}?availableQty=10", content: null);
        seed.EnsureSuccessStatusCode();

        using var reserve = await client.PostAsync($"/inventory/{productId}/reserve?quantity=4", content: null);
        reserve.EnsureSuccessStatusCode();
        await AssertStateAsync(client, productId, expectedAvailable: 6, expectedReserved: 4);

        using var release = await client.PostAsync($"/inventory/{productId}/release?quantity=1", content: null);
        release.EnsureSuccessStatusCode();
        await AssertStateAsync(client, productId, expectedAvailable: 7, expectedReserved: 3);

        using var commit = await client.PostAsync($"/inventory/{productId}/commit?quantity=2", content: null);
        commit.EnsureSuccessStatusCode();
        await AssertStateAsync(client, productId, expectedAvailable: 7, expectedReserved: 1);
    }

    [Theory]
    [InlineData("/reserve?quantity=0")]
    [InlineData("/reserve?quantity=-1")]
    [InlineData("/release?quantity=0")]
    [InlineData("/release?quantity=-1")]
    [InlineData("/commit?quantity=0")]
    [InlineData("/commit?quantity=-1")]
    public async Task Operations_WithInvalidQuantity_ReturnBadRequest(string suffix)
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var inventoryUrl = LiveTestSettings.GetServiceUrl("INVENTORY_SERVICE_URL", "http://localhost:5212");
        var productId = Guid.NewGuid();
        using var client = new HttpClient { BaseAddress = new Uri(inventoryUrl) };

        using var seed = await client.PutAsync($"/inventory/{productId}?availableQty=3", content: null);
        seed.EnsureSuccessStatusCode();

        using var response = await client.PostAsync($"/inventory/{productId}{suffix}", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reserve_InsufficientStock_ReturnsBadRequest()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var inventoryUrl = LiveTestSettings.GetServiceUrl("INVENTORY_SERVICE_URL", "http://localhost:5212");
        var productId = Guid.NewGuid();
        using var client = new HttpClient { BaseAddress = new Uri(inventoryUrl) };

        using var seed = await client.PutAsync($"/inventory/{productId}?availableQty=2", content: null);
        seed.EnsureSuccessStatusCode();

        using var reserve = await client.PostAsync($"/inventory/{productId}/reserve?quantity=5", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, reserve.StatusCode);
    }

    private static async Task AssertStateAsync(HttpClient client, Guid productId, int expectedAvailable, int expectedReserved)
    {
        using var state = await client.GetAsync($"/inventory/{productId}");
        state.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await state.Content.ReadAsStringAsync());

        Assert.Equal(expectedAvailable, json.RootElement.GetProperty("availableQty").GetInt32());
        Assert.Equal(expectedReserved, json.RootElement.GetProperty("reservedQty").GetInt32());
    }
}
