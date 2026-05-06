using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace CartService.Tests;

public sealed class CartServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("CART_SERVICE_URL", "http://localhost:5078"));

    [Fact]
    public async Task PutCart_WithMismatchedRouteAndBodyUserId_ReturnsBadRequest()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("CART_SERVICE_URL", "http://localhost:5078");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var routeUserId = Guid.NewGuid();

        using var response = await client.PutAsJsonAsync($"/carts/{routeUserId}", new
        {
            userId = Guid.NewGuid(),
            items = new object[]
            {
                new { productId = Guid.NewGuid(), quantity = 1, unitPrice = 10m }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutCart_Twice_OverwritesItemsAndKeepsSameCartId()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("CART_SERVICE_URL", "http://localhost:5078");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var userId = Guid.NewGuid();
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();

        using var put1 = await client.PutAsJsonAsync($"/carts/{userId}", new
        {
            userId,
            items = new object[] { new { productId = productA, quantity = 1, unitPrice = 5m } }
        });
        put1.EnsureSuccessStatusCode();
        using var firstJson = JsonDocument.Parse(await put1.Content.ReadAsStringAsync());
        var firstCartId = firstJson.RootElement.GetProperty("cartId").GetGuid();

        using var put2 = await client.PutAsJsonAsync($"/carts/{userId}", new
        {
            userId,
            items = new object[] { new { productId = productB, quantity = 2, unitPrice = 8m } }
        });
        put2.EnsureSuccessStatusCode();
        using var secondJson = JsonDocument.Parse(await put2.Content.ReadAsStringAsync());
        var secondCartId = secondJson.RootElement.GetProperty("cartId").GetGuid();
        var secondItems = secondJson.RootElement.GetProperty("items");

        Assert.Equal(firstCartId, secondCartId);
        Assert.Equal(1, secondItems.GetArrayLength());
        Assert.Equal(productB, secondItems[0].GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task DeleteCart_ThenGet_ReturnsEmptyCart()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("CART_SERVICE_URL", "http://localhost:5078");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var userId = Guid.NewGuid();

        using var put = await client.PutAsJsonAsync($"/carts/{userId}", new
        {
            userId,
            items = new object[] { new { productId = Guid.NewGuid(), quantity = 1, unitPrice = 7m } }
        });
        put.EnsureSuccessStatusCode();

        using var delete = await client.DeleteAsync($"/carts/{userId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using var get = await client.GetAsync($"/carts/{userId}");
        get.EnsureSuccessStatusCode();
        using var getJson = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(0, getJson.RootElement.GetProperty("items").GetArrayLength());
    }
}
