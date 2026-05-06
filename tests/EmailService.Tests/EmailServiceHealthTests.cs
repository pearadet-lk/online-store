using System.Net;
using System.Text.Json;
using TestInfrastructure;

namespace EmailService.Tests;

public sealed class EmailServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("EMAIL_SERVICE_URL", "http://localhost:5164"));

    [Fact]
    public async Task StatusEndpoints_ReturnExpectedShapes()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("EMAIL_SERVICE_URL", "http://localhost:5164");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var unknownOrderId = Guid.NewGuid();

        using var missing = await client.GetAsync($"/email/status/{unknownOrderId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var list = await client.GetAsync("/email/status");
        list.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, listJson.RootElement.ValueKind);
    }
}
