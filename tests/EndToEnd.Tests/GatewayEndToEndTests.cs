using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace EndToEnd.Tests;

public sealed class GatewayEndToEndTests
{
    private const string DemoUserEmail = "demo@example.com";
    private const string DemoUserPassword = "demo-password";

    [Fact]
    public async Task Gateway_HealthEndpoint_ReturnsServiceMetadata()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("gateway", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Gateway_DocsEndpoint_ReturnsDownstreamServiceList()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        using var response = await client.GetAsync("/api/docs");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("gateway", out _));
        Assert.True(json.RootElement.TryGetProperty("downstream", out var downstream));
        Assert.Equal(JsonValueKind.Array, downstream.ValueKind);
    }

    [Fact]
    public async Task Gateway_ProtectedRoute_RequiresValidJwt()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        using var response = await client.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Gateway_ProductsRoute_ProxiesWhenAuthorized()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        var token = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.GetAsync("/api/products");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task Gateway_IncludesCorrelationAndTraceHeaders()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", $"corr-{Guid.NewGuid():N}");

        using var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();

        Assert.True(response.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
        Assert.False(string.IsNullOrWhiteSpace(correlationValues.FirstOrDefault()));
        Assert.True(response.Headers.TryGetValues("X-Trace-Id", out var traceValues));
        Assert.False(string.IsNullOrWhiteSpace(traceValues.FirstOrDefault()));
    }

    [Fact]
    public async Task Gateway_RateLimit_EnforcedPerIp()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        var gotTooMany = false;
        for (var i = 0; i < 120; i++)
        {
            using var response = await client.GetAsync("/health");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                gotTooMany = true;
                break;
            }
        }

        Assert.True(gotTooMany, "Expected gateway 429 after exceeding per-IP limit.");
    }

    [Fact]
    public async Task Gateway_Health_PerformanceSmoke_UnderOneSecond()
    {
        if (!LiveTestSettings.IsEnabled) return;

        using var client = CreateGatewayClient();
        var sw = Stopwatch.StartNew();
        using var response = await client.GetAsync("/health");
        sw.Stop();

        response.EnsureSuccessStatusCode();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Expected /health < 1000ms, got {sw.ElapsedMilliseconds}ms.");
    }

    private static HttpClient CreateGatewayClient()
    {
        var gatewayUrl = LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152");
        return new HttpClient { BaseAddress = new Uri(gatewayUrl) };
    }

    private static async Task<string> LoginAsync(HttpClient client)
    {
        using var login = await client.PostAsJsonAsync("/api/users/login", new
        {
            email = DemoUserEmail,
            password = DemoUserPassword
        });
        login.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
    }
}
