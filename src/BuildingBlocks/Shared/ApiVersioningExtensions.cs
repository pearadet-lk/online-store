using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Shared;

public static class ApiVersioningExtensions
{
    public static PathString RewriteVersionedPath(PathString originalPath, string defaultApiVersion)
    {
        if (originalPath.StartsWithSegments($"/api/{defaultApiVersion}", out var remaining))
        {
            return remaining.HasValue ? remaining : "/";
        }

        return originalPath;
    }

    public static IApplicationBuilder UseDefaultApiVersioning(this IApplicationBuilder app, string defaultApiVersion = "v1")
    {
        return app.Use(async (context, next) =>
        {
            context.Request.Path = RewriteVersionedPath(context.Request.Path, defaultApiVersion);
            context.Response.Headers["api-supported-versions"] = defaultApiVersion;
            await next();
        });
    }
}
