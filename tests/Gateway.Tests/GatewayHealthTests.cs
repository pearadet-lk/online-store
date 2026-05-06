using TestInfrastructure;

namespace Gateway.Tests;

public sealed class GatewayHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("GATEWAY_SERVICE_URL", "http://localhost:5152"));
}
