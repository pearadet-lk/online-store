namespace Gateway.Middleware;

public sealed class LoggingMiddleware(RequestDelegate next, ILogger<LoggingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            logger.LogInformation(
                "Gateway request {Method} {Path} => {StatusCode} in {ElapsedMs}ms corr={CorrelationId}",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                sw.ElapsedMilliseconds,
                context.TraceIdentifier);
        }
    }
}

public static class LoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseGatewayRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<LoggingMiddleware>();
}
