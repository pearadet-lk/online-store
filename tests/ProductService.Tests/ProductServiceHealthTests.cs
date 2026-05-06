using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace ProductService.Tests;

public sealed class ProductServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("PRODUCT_SERVICE_URL", "http://localhost:5225"));

    [Fact]
    public async Task ProductCrud_Search_AndDeactivate_Workflow()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("PRODUCT_SERVICE_URL", "http://localhost:5225");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var productId = Guid.NewGuid();
        var uniqueToken = $"e2e-{Guid.NewGuid():N}";

        using var create = await client.PostAsJsonAsync("/products", new
        {
            productId,
            name = $"Product {uniqueToken}",
            description = $"Desc {uniqueToken}",
            price = 12.34m,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var get = await client.GetAsync($"/products/{productId}");
        get.EnsureSuccessStatusCode();
        using var getJson = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal(productId, getJson.RootElement.GetProperty("productId").GetGuid());

        using var search = await client.GetAsync($"/products?q={Uri.EscapeDataString(uniqueToken)}");
        search.EnsureSuccessStatusCode();
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        Assert.True(ArrayContainsProductId(searchJson.RootElement, productId));

        using var update = await client.PutAsJsonAsync($"/products/{productId}", new
        {
            productId = Guid.Empty,
            name = $"Updated {uniqueToken}",
            description = $"Updated Desc {uniqueToken}",
            price = 22.22m,
            isActive = true
        });
        update.EnsureSuccessStatusCode();
        using var updateJson = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        Assert.Equal(productId, updateJson.RootElement.GetProperty("productId").GetGuid());
        Assert.Equal(22.22m, updateJson.RootElement.GetProperty("price").GetDecimal());

        using var deactivate = await client.DeleteAsync($"/products/{productId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivate.StatusCode);

        using var adminList = await client.GetAsync($"/admin/products?q={Uri.EscapeDataString(uniqueToken)}");
        adminList.EnsureSuccessStatusCode();
        using var adminJson = JsonDocument.Parse(await adminList.Content.ReadAsStringAsync());
        Assert.True(ArrayContainsInactiveProduct(adminJson.RootElement, productId));
    }

    [Fact]
    public async Task CreateOrUpdate_WithNegativePrice_ReturnsBadRequest()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("PRODUCT_SERVICE_URL", "http://localhost:5225");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

        using var create = await client.PostAsJsonAsync("/products", new
        {
            productId = Guid.NewGuid(),
            name = "Invalid Price",
            description = "Should fail",
            price = -1m,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var existingProductId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        using var update = await client.PutAsJsonAsync($"/products/{existingProductId}", new
        {
            productId = existingProductId,
            name = "Invalid Update",
            description = "Should fail",
            price = -10m,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }

    private static bool ArrayContainsProductId(JsonElement arrayElement, Guid productId) =>
        arrayElement.ValueKind == JsonValueKind.Array &&
        arrayElement.EnumerateArray().Any(x =>
            x.TryGetProperty("productId", out var idProp) && idProp.GetGuid() == productId);

    private static bool ArrayContainsInactiveProduct(JsonElement arrayElement, Guid productId) =>
        arrayElement.ValueKind == JsonValueKind.Array &&
        arrayElement.EnumerateArray().Any(x =>
            x.TryGetProperty("productId", out var idProp) &&
            x.TryGetProperty("isActive", out var activeProp) &&
            idProp.GetGuid() == productId &&
            activeProp.ValueKind == JsonValueKind.False);
}
