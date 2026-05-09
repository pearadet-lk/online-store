using System.Security.Claims;

namespace Gateway.Middleware;

public sealed class JwtClaimForwardMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                         context.User.FindFirstValue("sub");
            var email = context.User.FindFirstValue(ClaimTypes.Email) ??
                        context.User.FindFirstValue("email");
            var name = context.User.FindFirstValue(ClaimTypes.Name);

            SetForwardedHeader(context, "X-Authenticated-UserId", userId);
            SetForwardedHeader(context, "X-Authenticated-Email", email);
            SetForwardedHeader(context, "X-Authenticated-Name", name);
        }

        await next(context);
    }

    private static void SetForwardedHeader(HttpContext context, string headerName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            context.Request.Headers.Remove(headerName);
            return;
        }

        context.Request.Headers[headerName] = value;
    }
}

public static class JwtClaimForwardMiddlewareExtensions
{
    public static IApplicationBuilder UseJwtClaimForwarding(this IApplicationBuilder app) =>
        app.UseMiddleware<JwtClaimForwardMiddleware>();
}
