using TestInfrastructure;

namespace UserService.Tests;

public sealed class UserServiceHealthTests
{
    [Fact]
    public Task HealthEndpoint_ReturnsSuccess() =>
        LiveTestSettings.AssertHealthEndpointAsync(
            LiveTestSettings.GetServiceUrl("USER_SERVICE_URL", "http://localhost:5121"));
}
