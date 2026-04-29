using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace Shared;

public static class GlobalExceptionHandlingExtensions
{
    public static IApplicationBuilder UseGlobalExceptionHandling(this IApplicationBuilder app, string serviceName)
    {
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionFeature?.Error;
                var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                var statusCode = MapStatusCode(exception, context);
                var title = GetTitle(statusCode);
                var detail = statusCode < 500 ? exception?.Message : null;

                if (statusCode >= 500)
                {
                    Log.Error(
                        exception,
                        "Unhandled exception in {ServiceName}. TraceId: {TraceId}",
                        serviceName,
                        traceId);
                }
                else
                {
                    Log.Warning(
                        exception,
                        "Handled exception mapped to {StatusCode} in {ServiceName}. TraceId: {TraceId}",
                        statusCode,
                        serviceName,
                        traceId);
                }

                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = $"https://httpstatuses.com/{statusCode}",
                    title,
                    status = statusCode,
                    traceId,
                    detail
                });
            });
        });

        return app;
    }

    private static int MapStatusCode(Exception? exception, HttpContext context)
    {
        if (exception is null)
        {
            return StatusCodes.Status500InternalServerError;
        }

        return exception switch
        {
            UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
            KeyNotFoundException => StatusCodes.Status404NotFound,
            ArgumentException => StatusCodes.Status400BadRequest,
            FormatException => StatusCodes.Status400BadRequest,
            JsonException => StatusCodes.Status400BadRequest,
            BadHttpRequestException => StatusCodes.Status400BadRequest,
            InvalidOperationException => StatusCodes.Status409Conflict,
            TimeoutException => StatusCodes.Status408RequestTimeout,
            HttpRequestException => StatusCodes.Status503ServiceUnavailable,
            OperationCanceledException when context.RequestAborted.IsCancellationRequested => 499, // Client closed request
            OperationCanceledException => StatusCodes.Status408RequestTimeout,
            _ => StatusCodes.Status500InternalServerError
        };
    }

    private static string GetTitle(int statusCode) =>
        statusCode switch
        {
            StatusCodes.Status400BadRequest => "The request is invalid.",
            StatusCodes.Status401Unauthorized => "Authentication is required.",
            StatusCodes.Status404NotFound => "The requested resource was not found.",
            StatusCodes.Status408RequestTimeout => "The request timed out.",
            StatusCodes.Status409Conflict => "The request conflicts with current state.",
            499 => "The client closed the request.",
            StatusCodes.Status503ServiceUnavailable => "A downstream dependency is unavailable.",
            _ => "An unexpected error occurred."
        };
}
