using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Shared;
using Xunit;

namespace ApiVersioning.Tests;

public class ApiVersioningIntegrationTests
{
    [Fact]
    public async Task VersionedRoute_ReturnsSameEndpointResponse_AndVersionHeader()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.Configure(app =>
                {
                    app.UseDefaultApiVersioning("v1");
                    app.Run(async context =>
                    {
                        await context.Response.WriteAsync(context.Request.Path.Value ?? string.Empty);
                    });
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetAsync("/api/v1/health");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("/health", body);
        Assert.Equal("v1", response.Headers.GetValues("api-supported-versions").Single());
    }

    [Fact]
    public async Task UnversionedRoute_RemainsBackwardCompatible()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.Configure(app =>
                {
                    app.UseDefaultApiVersioning("v1");
                    app.Run(async context =>
                    {
                        await context.Response.WriteAsync(context.Request.Path.Value ?? string.Empty);
                    });
                });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("/health", body);
        Assert.Equal("v1", response.Headers.GetValues("api-supported-versions").Single());
    }
}
