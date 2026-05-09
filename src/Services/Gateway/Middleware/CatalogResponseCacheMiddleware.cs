using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Gateway.Middleware;

public sealed class CatalogResponseCacheMiddleware(RequestDelegate next, IMemoryCache cache)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    public async Task Invoke(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !(context.Request.Path.StartsWithSegments("/api/products") ||
              context.Request.Path.StartsWithSegments("/api/admin/products")))
        {
            await next(context);
            return;
        }

        var cacheKey = $"catalog:{context.Request.Path}{context.Request.QueryString}";
        if (cache.TryGetValue<CachedResponse>(cacheKey, out var cached) && cached is not null)
        {
            context.Response.StatusCode = cached.StatusCode;
            context.Response.ContentType = cached.ContentType;
            context.Response.Headers["X-Gateway-Cache"] = "HIT";
            await context.Response.WriteAsync(cached.Body);
            return;
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
            if (context.Response.StatusCode is >= 200 and < 300)
            {
                buffer.Position = 0;
                var body = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();
                cache.Set(
                    cacheKey,
                    new CachedResponse(context.Response.StatusCode, context.Response.ContentType ?? "application/json", body),
                    CacheTtl);
                context.Response.Headers["X-Gateway-Cache"] = "MISS";
                buffer.Position = 0;
            }

            await buffer.CopyToAsync(originalBody);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private sealed record CachedResponse(int StatusCode, string ContentType, string Body);
}

public static class CatalogResponseCacheMiddlewareExtensions
{
    public static IApplicationBuilder UseCatalogResponseCache(this IApplicationBuilder app) =>
        app.UseMiddleware<CatalogResponseCacheMiddleware>();
}
