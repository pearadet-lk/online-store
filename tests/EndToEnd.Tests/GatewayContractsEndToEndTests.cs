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
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

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
        Assert.True(json.RootElement.TryGetProperty("accessTokenExpiresAt", out var expiresAt) && expiresAt.ValueKind == JsonValueKind.String);
        Assert.True(json.RootElement.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object);
        Assert.True(user.TryGetProperty("userId", out _));
        Assert.True(user.TryGetProperty("email", out _));
        Assert.True(user.TryGetProperty("fullName", out _));
        Assert.True(user.TryGetProperty("createdAt", out _));
    }

    [Fact]
    public async Task Cart_Get_ResponseMatchesExpectedContract()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        using var client = CreateGatewayClient();
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
        Assert.True(json.RootElement.TryGetProperty("userId", out var userIdProp) && userIdProp.GetGuid() == userId);
        Assert.True(json.RootElement.TryGetProperty("updatedAt", out _));
        Assert.True(json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array);
        Assert.True(items.GetArrayLength() >= 1);
        var firstItem = items[0];
        Assert.True(firstItem.TryGetProperty("productId", out _));
        Assert.True(firstItem.TryGetProperty("quantity", out _));
        Assert.True(firstItem.TryGetProperty("unitPrice", out _));
    }

    [Fact]
    public async Task AdminProducts_ResponseItemsMatchExpectedContract()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        using var client = CreateGatewayClient();
        using var response = await client.GetAsync("/api/admin/products");
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
    public async Task Checkout_ResponseMatchesExpectedContract()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        using var client = CreateGatewayClient();
        var (accessToken, userId) = await LoginAsync(client);

        using var cart = await client.PutAsJsonAsync($"/api/carts/{userId}", new
        {
            userId,
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        });
        cart.EnsureSuccessStatusCode();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout")
        {
            Content = JsonContent.Create(new
            {
                userId,
                currency = "USD",
                items = new object[]
                {
                    new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
                }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Idempotency-Key", $"contract-{Guid.NewGuid():N}");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(json.RootElement.TryGetProperty("sagaId", out _));
        Assert.True(json.RootElement.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Object);
        Assert.True(order.TryGetProperty("orderId", out _));
        Assert.True(order.TryGetProperty("userId", out _));
        Assert.True(order.TryGetProperty("totalAmount", out _));
        Assert.True(order.TryGetProperty("currency", out _));
        Assert.True(order.TryGetProperty("status", out _));
        Assert.True(json.RootElement.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object);
        Assert.True(payment.TryGetProperty("paymentId", out _));
        Assert.True(payment.TryGetProperty("orderId", out _));
        Assert.True(payment.TryGetProperty("amount", out _));
        Assert.True(payment.TryGetProperty("currency", out _));
        Assert.True(payment.TryGetProperty("status", out _));
        Assert.True(payment.TryGetProperty("providerReference", out _));
        Assert.True(json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "CheckoutCompleted");
    }

    private static HttpClient CreateGatewayClient()
    {
        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        return new HttpClient { BaseAddress = new Uri(gatewayUrl) };
    }

    private static async Task<(string AccessToken, Guid UserId)> LoginAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/users/login", new
        {
            email = DemoUserEmail,
            password = DemoUserPassword
        });
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var accessToken = json.RootElement.GetProperty("accessToken").GetString()!;
        var userId = json.RootElement.GetProperty("user").GetProperty("userId").GetGuid();
        return (accessToken, userId);
    }
}
