using Microsoft.AspNetCore.Http;
using Shared;
using Xunit;

namespace ApiVersioning.Tests;

public class ApiVersioningExtensionsUnitTests
{
    [Theory]
    [InlineData("/api/v1/health", "/health")]
    [InlineData("/api/v1/orders/123", "/orders/123")]
    [InlineData("/health", "/health")]
    public void RewriteVersionedPath_RewritesExpectedPath(string input, string expected)
    {
        var rewritten = ApiVersioningExtensions.RewriteVersionedPath(new PathString(input), "v1");
        Assert.Equal(expected, rewritten.Value);
    }

    [Fact]
    public void RewriteVersionedPath_RewritesVersionRootToSlash()
    {
        var rewritten = ApiVersioningExtensions.RewriteVersionedPath(new PathString("/api/v1"), "v1");
        Assert.Equal("/", rewritten.Value);
    }
}
