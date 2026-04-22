using System.Collections.Concurrent;
using System.Diagnostics;
using Contracts;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;

const string ServiceName = "user-service";
var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddSingleton<UserStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

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

app.MapGet("/health", () => Results.Ok(new { service = "user-service", status = "ok" }));
app.MapMetrics("/metrics");

app.MapPost("/users/register", (UserRegistrationRequest request, UserStore store) =>
{
    if (store.UsersByEmail.ContainsKey(request.Email))
    {
        return Results.Conflict(new { error = "Email already exists." });
    }

    var profile = new UserProfileDto(Guid.NewGuid(), request.Email, request.FullName, DateTimeOffset.UtcNow);
    store.UsersById[profile.UserId] = profile;
    store.UsersByEmail[profile.Email] = profile;

    return Results.Created($"/users/{profile.UserId}", profile);
});

app.MapPost("/users/login", (UserLoginRequest request, UserStore store) =>
{
    _ = request.Password; // Replace with real password verification/JWT creation.
    if (!store.UsersByEmail.TryGetValue(request.Email, out var profile))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new
    {
        accessToken = $"demo-jwt-{profile.UserId:N}",
        user = profile
    });
});

app.MapGet("/users/{userId:guid}", (Guid userId, UserStore store) =>
{
    return store.UsersById.TryGetValue(userId, out var profile)
        ? Results.Ok(profile)
        : Results.NotFound();
});

app.Run();

internal sealed class UserStore
{
    public ConcurrentDictionary<Guid, UserProfileDto> UsersById { get; } = new(
        new[]
        {
            new KeyValuePair<Guid, UserProfileDto>(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                new UserProfileDto(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    "demo@example.com",
                    "Demo User",
                    DateTimeOffset.UtcNow))
        });

    public ConcurrentDictionary<string, UserProfileDto> UsersByEmail { get; } = new(
        new[]
        {
            new KeyValuePair<string, UserProfileDto>(
                "demo@example.com",
                new UserProfileDto(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    "demo@example.com",
                    "Demo User",
                    DateTimeOffset.UtcNow))
        },
        StringComparer.OrdinalIgnoreCase);
}
