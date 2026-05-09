using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Gateway.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;
using Yarp.ReverseProxy.Transforms;

const string ServiceName = "gateway";

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Configuration.AddJsonFile("ReverseProxy/yarp.json", optional: false, reloadOnChange: true);
    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        var elasticsearchUrl = context.Configuration["Observability:ElasticsearchUrl"] ?? "http://elasticsearch:9200";
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", ServiceName)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Service}] [TraceId:{TraceId}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Elasticsearch(new ElasticsearchSinkOptions(new Uri(elasticsearchUrl))
            {
                AutoRegisterTemplate = true,
                IndexFormat = $"online-store-{ServiceName}-logs-{DateTime.UtcNow:yyyy.MM}"
            });
    });
    builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(ServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Endpoint = new Uri(builder.Configuration["Observability:OtlpEndpoint"] ?? "http://jaeger:4317");
            }));
    builder.Services.AddOpenApi();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var jwtIssuer = builder.Configuration["Auth:JwtIssuer"] ?? "online-store";
    var jwtAudience = builder.Configuration["Auth:JwtAudience"] ?? "online-store-clients";
    var jwtSigningKey = builder.Configuration["Auth:JwtSigningKey"] ?? "dev-super-secret-signing-key-min-32-chars";
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtIssuer,
                ValidateAudience = true,
                ValidAudience = jwtAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("authenticated", policy => policy.RequireAuthenticatedUser());
    });
    builder.Services.AddMemoryCache();

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy("gateway-subject-or-ip", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.Identity?.IsAuthenticated == true
                    ? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? context.User.FindFirstValue("sub")
                        ?? context.Connection.RemoteIpAddress?.ToString()
                        ?? "anonymous"
                    : context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
    });
    builder.Services.AddReverseProxy()
        .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
        .AddTransforms(transformBuilderContext =>
        {
            transformBuilderContext.AddRequestTransform(async transformContext =>
            {
                var correlationId = transformContext.HttpContext.TraceIdentifier;
                transformContext.ProxyRequest.Headers.Remove("X-Correlation-ID");
                transformContext.ProxyRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
                await Task.CompletedTask;
            });
        });

    var app = builder.Build();

    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1
    };
    forwardedHeadersOptions.KnownProxies.Clear();

    var trustedProxies = app.Configuration.GetSection("Gateway:TrustedProxies").Get<string[]>() ?? [];
    foreach (var proxy in trustedProxies)
    {
        if (System.Net.IPAddress.TryParse(proxy, out var ip))
        {
            forwardedHeadersOptions.KnownProxies.Add(ip);
        }
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseGlobalExceptionHandling(ServiceName);
    app.UseForwardedHeaders(forwardedHeadersOptions);
    app.UseCorrelationId();
    app.UseGatewayRequestLogging();
    app.Use(async (context, next) =>
    {
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-Id"] = traceId;
        using (LogContext.PushProperty("TraceId", traceId))
        {
            await next();
        }
    });
    app.UseSerilogRequestLogging();
    app.UseHttpMetrics();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseJwtClaimForwarding();
    app.UseRateLimiter();
    app.UseCatalogResponseCache();

    app.MapGet("/health", () => Results.Ok(new { service = "gateway", status = "ok" }));
    app.MapMetrics("/metrics");
    app.MapGet("/api/docs", (IConfiguration configuration) =>
        Results.Ok(new
        {
            gateway = "/swagger",
            downstream =
                configuration.GetSection("Swagger:Services").GetChildren()
                    .Select(x => new { name = x.Key, url = x.Value })
        }));
    app.MapReverseProxy().RequireRateLimiting("gateway-subject-or-ip");

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Gateway host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
