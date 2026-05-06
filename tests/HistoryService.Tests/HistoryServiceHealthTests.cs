using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TestInfrastructure;

namespace HistoryService.Tests;

public sealed class HistoryServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("HISTORY_SERVICE_URL", "http://localhost:5029"));

    [Fact]
    public async Task CreateHistoryEvent_ThenListByUser_ReturnsEvent()
    {
        if (!LiveTestSettings.IsEnabled)
        {
            return;
        }

        var baseUrl = LiveTestSettings.GetServiceUrl("HISTORY_SERVICE_URL", "http://localhost:5029");
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var userId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var notes = $"history-{Guid.NewGuid():N}";

        using var create = await client.PostAsJsonAsync("/history/events", new
        {
            historyId = Guid.Empty,
            orderId,
            userId,
            eventType = "CheckoutCompleted",
            createdAt = default(DateTimeOffset),
            notes
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var list = await client.GetAsync($"/history/users/{userId}");
        list.EnsureSuccessStatusCode();
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, listJson.RootElement.ValueKind);
        Assert.True(listJson.RootElement.GetArrayLength() >= 1);
        Assert.Contains(listJson.RootElement.EnumerateArray(), e =>
            e.GetProperty("orderId").GetGuid() == orderId &&
            e.GetProperty("notes").GetString() == notes);
    }
}
