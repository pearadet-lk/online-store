using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace EndToEnd.Tests;

public sealed class GatewayContractsEndToEndTests
{
    private const string DemoUserEmail = "demo@example.com";
    private const string DemoUserPassword = "demo-password";
    private static readonly Guid ProductId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Login_ResponseMatchesExpectedContract()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        using var response = await client.PostAsJsonAsync("/api/users/login", new
        {
            email = DemoUserEmail,
            password = DemoUserPassword
        });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("accessToken", out var accessToken) && accessToken.ValueKind == JsonValueKind.String);
        Assert.True(json.RootElement.TryGetProperty("refreshToken", out var refreshToken) && refreshToken.ValueKind == JsonValueKind.String);
        Assert.True(json.RootElement.TryGetProperty("accessTokenExpiresAt", out _));
        Assert.True(json.RootElement.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object);
        Assert.True(user.TryGetProperty("userId", out _));
        Assert.True(user.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task Products_ResponseItemsMatchExpectedContract()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/products");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.True(json.RootElement.GetArrayLength() > 0);
        var first = json.RootElement[0];
        Assert.True(first.TryGetProperty("productId", out _));
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("description", out _));
        Assert.True(first.TryGetProperty("price", out _));
        Assert.True(first.TryGetProperty("isActive", out _));
    }

    [Fact]
    public async Task Cart_Get_ResponseMatchesExpectedContract()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var userId = Guid.NewGuid();

        using var put = await client.PutAsJsonAsync($"/api/carts/{userId}", new
        {
            userId,
            items = new object[]
            {
                new { productId = ProductId1, quantity = 2, unitPrice = 39.99m }
            }
        });
        put.EnsureSuccessStatusCode();

        using var get = await client.GetAsync($"/api/carts/{userId}");
        get.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("cartId", out _));
        Assert.True(json.RootElement.TryGetProperty("userId", out var idProp) && idProp.GetGuid() == userId);
        Assert.True(json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array);
    }

    private static HttpClient CreateGatewayClient()
    {
        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        return new HttpClient { BaseAddress = new Uri(gatewayUrl) };
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/users/login", new
        {
            email = DemoUserEmail,
            password = DemoUserPassword
        });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
