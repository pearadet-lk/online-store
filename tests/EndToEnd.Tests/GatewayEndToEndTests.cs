using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;
using TestInfrastructure;
using Xunit;

namespace EndToEnd.Tests;

public sealed class GatewayEndToEndTests
{
    private const string DemoUserEmail = "demo@example.com";
    private const string DemoUserPassword = "demo-password";
    private static readonly Guid ProductId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProductId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Gateway_HealthEndpoint_ReturnsServiceMetadata()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("gateway", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Gateway_ProductsEndpoint_ProxiesProductService()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        using var response = await client.GetAsync("/api/products");
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(payload);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task Gateway_HealthAndCatalog_IncludeTraceIdHeader()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        Assert.True(health.Headers.TryGetValues("X-Trace-Id", out var healthTraceValues));
        Assert.False(string.IsNullOrWhiteSpace(healthTraceValues.FirstOrDefault()));

        using var catalog = await client.GetAsync("/api/products");
        catalog.EnsureSuccessStatusCode();
        Assert.True(catalog.Headers.TryGetValues("X-Trace-Id", out var catalogTraceValues));
        Assert.False(string.IsNullOrWhiteSpace(catalogTraceValues.FirstOrDefault()));
    }

    [Fact]
    public async Task Gateway_Checkout_WithValidAuth_CompletesAndSupportsIdempotentReplay()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        var (accessToken, userId) = await LoginAsync(client);
        using var cartResponse = await UpsertCartAsync(client, userId);
        cartResponse.EnsureSuccessStatusCode();

        var checkoutRequest = new
        {
            userId,
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m },
                new { productId = ProductId2, quantity = 1, unitPrice = 59.99m }
            }
        };

        var idempotencyKey = $"e2e-{Guid.NewGuid():N}";
        var first = await PostCheckoutAsync(client, accessToken, idempotencyKey, checkoutRequest);
        first.EnsureSuccessStatusCode();
        var firstBody = await first.Content.ReadAsStringAsync();

        var second = await PostCheckoutAsync(client, accessToken, idempotencyKey, checkoutRequest);
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadAsStringAsync();

        using var firstJson = JsonDocument.Parse(firstBody);
        using var secondJson = JsonDocument.Parse(secondBody);

        Assert.Equal("CheckoutCompleted", firstJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            firstJson.RootElement.GetProperty("order").GetProperty("orderId").GetGuid(),
            secondJson.RootElement.GetProperty("order").GetProperty("orderId").GetGuid());
        Assert.True(second.Headers.TryGetValues("X-Idempotent-Replay", out _));
    }

    [Fact]
    public async Task Gateway_Checkout_ResponseContainsExpectedContractShape()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var (accessToken, userId) = await LoginAsync(client);
        using var cartResponse = await UpsertCartAsync(client, userId);
        cartResponse.EnsureSuccessStatusCode();

        var request = new
        {
            userId,
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        };

        using var response = await PostCheckoutAsync(client, accessToken, $"contract-{Guid.NewGuid():N}", request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(json.RootElement.TryGetProperty("sagaId", out var sagaId) && sagaId.ValueKind == JsonValueKind.String);
        Assert.True(json.RootElement.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Object);
        Assert.True(order.TryGetProperty("orderId", out _));
        Assert.True(order.TryGetProperty("totalAmount", out _));
        Assert.True(order.TryGetProperty("currency", out _));
        Assert.True(json.RootElement.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object);
        Assert.True(payment.TryGetProperty("paymentId", out _));
        Assert.True(payment.TryGetProperty("status", out _));
        Assert.True(json.RootElement.TryGetProperty("status", out var status) && status.GetString() == "CheckoutCompleted");
    }

    [Fact]
    public async Task Gateway_Products_ResponseItemsContainExpectedContractFields()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

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
    public async Task Gateway_CatalogRateLimit_ReturnsTooManyRequests_WhenBurstExceeded()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var got429 = false;

        for (var i = 0; i < 105; i++)
        {
            using var response = await client.GetAsync("/api/products");
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                got429 = true;
                break;
            }
        }

        Assert.True(got429, "Expected at least one 429 response after exceeding catalog rate limit.");
    }

    [Fact]
    public async Task Gateway_CheckoutRateLimit_ReturnsTooManyRequests_ForAnonymousBurst()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var payload = new
        {
            userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            currency = "USD",
            items = new object[] { new { productId = ProductId1, quantity = 1, unitPrice = 39.99m } }
        };

        var got429 = false;
        for (var i = 0; i < 8; i++)
        {
            using var response = await PostCheckoutAsync(client, accessToken: null, $"limit-{Guid.NewGuid():N}", payload);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                got429 = true;
                break;
            }
        }

        Assert.True(got429, "Expected at least one 429 response after exceeding checkout rate limit.");
    }

    [Fact]
    public async Task Gateway_Checkout_WhenReserveFails_CompensatesInventory()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        var inventoryUrl = LiveTestSettings.GetServiceUrl("INVENTORY_SERVICE_URL", "http://localhost:5212");
        using var gatewayClient = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        using var inventoryClient = new HttpClient { BaseAddress = new Uri(inventoryUrl) };

        // Set a deterministic starting point so reserve compensation can be asserted.
        using var seed = await inventoryClient.PutAsync($"/inventory/{ProductId1}?availableQty=100", content: null);
        seed.EnsureSuccessStatusCode();

        var (accessToken, userId) = await LoginAsync(gatewayClient);
        var failingItems = new object[]
        {
            new { productId = ProductId1, quantity = 60, unitPrice = 39.99m },
            new { productId = ProductId1, quantity = 60, unitPrice = 39.99m }
        };

        using var cartResponse = await gatewayClient.PutAsJsonAsync($"/api/carts/{userId}", new
        {
            userId,
            items = failingItems
        });
        cartResponse.EnsureSuccessStatusCode();

        using var checkout = await PostCheckoutAsync(gatewayClient, accessToken, $"comp-{Guid.NewGuid():N}", new
        {
            userId,
            currency = "USD",
            items = failingItems
        });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, checkout.StatusCode);

        using var inventory = await inventoryClient.GetAsync($"/inventory/{ProductId1}");
        inventory.EnsureSuccessStatusCode();
        using var inventoryJson = JsonDocument.Parse(await inventory.Content.ReadAsStringAsync());
        Assert.Equal(100, inventoryJson.RootElement.GetProperty("availableQty").GetInt32());
        Assert.Equal(0, inventoryJson.RootElement.GetProperty("reservedQty").GetInt32());
    }

    [Fact]
    public async Task Gateway_Checkout_WithoutAuthorization_ReturnsUnauthorized()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        var request = new
        {
            userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        };

        using var response = await PostCheckoutAsync(client, accessToken: null, $"e2e-{Guid.NewGuid():N}", request);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Gateway_Checkout_WithDifferentUserThanToken_ReturnsForbidden()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var (accessToken, _) = await LoginAsync(client);

        var request = new
        {
            userId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        };

        using var response = await PostCheckoutAsync(client, accessToken, $"e2e-{Guid.NewGuid():N}", request);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Gateway_Checkout_WithInvalidJwt_ReturnsUnauthorized()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        var request = new
        {
            userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        };

        using var response = await PostCheckoutAsync(client, "not-a-real-jwt", $"e2e-{Guid.NewGuid():N}", request);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Gateway_UserRefreshAndLogout_FlowWorksWithRefreshToken()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };

        using var login = await client.PostAsJsonAsync("/api/users/login", new
        {
            email = DemoUserEmail,
            password = DemoUserPassword
        });
        login.EnsureSuccessStatusCode();
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var refreshToken = loginJson.RootElement.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));

        using var refresh = await client.PostAsJsonAsync("/api/users/refresh", new
        {
            refreshToken
        });
        refresh.EnsureSuccessStatusCode();
        using var refreshJson = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        var rotatedRefreshToken = refreshJson.RootElement.GetProperty("refreshToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(rotatedRefreshToken));
        Assert.NotEqual(refreshToken, rotatedRefreshToken);

        using var logout = await client.PostAsJsonAsync("/api/users/logout", new
        {
            refreshToken = rotatedRefreshToken
        });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, logout.StatusCode);

        using var replayRefresh = await client.PostAsJsonAsync("/api/users/refresh", new
        {
            refreshToken = rotatedRefreshToken
        });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, replayRefresh.StatusCode);
    }

    [Fact]
    public async Task Gateway_Checkout_WithCartMismatch_ReturnsBadRequest()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var (accessToken, userId) = await LoginAsync(client);

        using var cartResponse = await client.PutAsJsonAsync($"/api/carts/{userId}", new
        {
            userId,
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        });
        cartResponse.EnsureSuccessStatusCode();

        using var response = await PostCheckoutAsync(client, accessToken, $"mismatch-{Guid.NewGuid():N}", new
        {
            userId,
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 2, unitPrice = 39.99m }
            }
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Gateway_Checkout_SameIdempotencyKeyDifferentPayload_ReturnsConflict()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var (accessToken, userId) = await LoginAsync(client);
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";

        using var cartResponse = await client.PutAsJsonAsync($"/api/carts/{userId}", new
        {
            userId,
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        });
        cartResponse.EnsureSuccessStatusCode();

        using var first = await PostCheckoutAsync(client, accessToken, idempotencyKey, new
        {
            userId,
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m }
            }
        });
        first.EnsureSuccessStatusCode();

        using var second = await PostCheckoutAsync(client, accessToken, idempotencyKey, new
        {
            userId,
            currency = "USD",
            items = new object[]
            {
                new { productId = ProductId1, quantity = 2, unitPrice = 39.99m }
            }
        });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Gateway_Health_PerformanceSmoke_UnderOneSecond()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.GetAsync("/health");
        stopwatch.Stop();

        response.EnsureSuccessStatusCode();
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, $"Expected /health < 1000ms, got {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Gateway_Products_PerformanceSmoke_UnderTwoSeconds()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        using var client = new HttpClient { BaseAddress = new Uri(gatewayUrl) };
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.GetAsync("/api/products");
        stopwatch.Stop();

        response.EnsureSuccessStatusCode();
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Expected /api/products < 2000ms, got {stopwatch.ElapsedMilliseconds}ms.");
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

    private static Task<HttpResponseMessage> UpsertCartAsync(HttpClient client, Guid userId) =>
        client.PutAsJsonAsync($"/api/carts/{userId}", new
        {
            userId,
            items = new object[]
            {
                new { productId = ProductId1, quantity = 1, unitPrice = 39.99m },
                new { productId = ProductId2, quantity = 1, unitPrice = 59.99m }
            }
        });

    private static Task<HttpResponseMessage> PostCheckoutAsync(HttpClient client, string? accessToken, string idempotencyKey, object requestBody)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/checkout")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client.SendAsync(request);
    }
}
