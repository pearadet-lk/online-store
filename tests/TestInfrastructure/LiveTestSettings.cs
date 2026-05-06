global using Xunit;

namespace TestInfrastructure;

internal static class LiveTestSettings
{
    private const string EnableFlagName = "RUN_LIVE_SERVICE_TESTS";

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableFlagName), "true", StringComparison.OrdinalIgnoreCase);

    public static async Task AssertHealthEndpointAsync(string serviceUrl)
    {
        if (!IsEnabled)
        {
            return;
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri(serviceUrl)
        };

        using var response = await client.GetAsync("/health");
        Assert.True(response.IsSuccessStatusCode, $"Expected 2xx from {serviceUrl}/health but got {(int)response.StatusCode}.");
    }

    public static string GetServiceUrl(string envVar, string defaultUrl) =>
        Environment.GetEnvironmentVariable(envVar) ?? defaultUrl;
}
