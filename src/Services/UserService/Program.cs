using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Contracts;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;
using Serilog;
using Serilog.Context;
using Serilog.Sinks.Elasticsearch;
using Shared;

const string ServiceName = "user-service";
const string DefaultApiVersion = "v1";
const string DemoEmail = "demo@example.com";
const string DemoPassword = "demo-password";
const string DemoFullName = "Demo User";
var demoUserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

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
        .AddOnlineStoreTraceExporters(builder.Configuration));
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AuthTokenService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton(new UserAuthOptions(
    ConnectionString: builder.Configuration.GetConnectionString("Users") ?? builder.Configuration.GetConnectionString("Catalog"),
    JwtIssuer: builder.Configuration["Auth:JwtIssuer"] ?? "online-store",
    JwtAudience: builder.Configuration["Auth:JwtAudience"] ?? "online-store-clients",
    JwtSigningKey: builder.Configuration["Auth:JwtSigningKey"] ?? "dev-super-secret-signing-key-min-32-chars",
    AccessTokenMinutes: Math.Clamp(builder.Configuration.GetValue("Auth:AccessTokenMinutes", 15), 1, 120),
    RefreshTokenDays: Math.Clamp(builder.Configuration.GetValue("Auth:RefreshTokenDays", 7), 1, 90)));

var app = builder.Build();
var options = app.Services.GetRequiredService<UserAuthOptions>();
var store = app.Services.GetRequiredService<UserStore>();
var hasher = app.Services.GetRequiredService<PasswordHasher>();

if (!string.IsNullOrWhiteSpace(options.ConnectionString))
{
    try
    {
        await UserAuthDb.EnsureSchemaAsync(options.ConnectionString, demoUserId, DemoEmail, DemoFullName, hasher.Hash(DemoPassword), CancellationToken.None);
        app.Logger.LogInformation("User auth store initialized in PostgreSQL.");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "User auth PostgreSQL unavailable; using in-memory fallback.");
        options.ConnectionString = null;
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseGlobalExceptionHandling(ServiceName);

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
app.UseDefaultApiVersioning(DefaultApiVersion);
app.UseRouting();

app.MapGet("/health", () => Results.Ok(new { service = "user-service", status = "ok" }));
app.MapMetrics("/metrics");

app.MapPost("/users/register", async (UserRegistrationRequest request, PasswordHasher passwordHasher, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.BadRequest(new { error = "Email and password are required." });
    }

    var normalizedEmail = request.Email.Trim().ToLowerInvariant();
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        var existing = await UserAuthDb.GetUserByEmailAsync(options.ConnectionString, normalizedEmail, ct);
        if (existing is not null)
        {
            return Results.Conflict(new { error = "Email already exists." });
        }

        var created = await UserAuthDb.CreateUserAsync(
            options.ConnectionString,
            normalizedEmail,
            request.FullName.Trim(),
            passwordHasher.Hash(request.Password),
            ct);
        return Results.Created($"/users/{created.Profile.UserId}", created.Profile);
    }

    if (store.UsersByEmail.ContainsKey(normalizedEmail))
    {
        return Results.Conflict(new { error = "Email already exists." });
    }

    var profile = new UserProfileDto(Guid.NewGuid(), normalizedEmail, request.FullName, DateTimeOffset.UtcNow);
    var account = new UserAccount(profile, passwordHasher.Hash(request.Password));
    store.UsersById[profile.UserId] = account;
    store.UsersByEmail[normalizedEmail] = account;

    return Results.Created($"/users/{profile.UserId}", profile);
});

app.MapPost("/users/login", async (UserLoginRequest request, AuthTokenService tokenService, PasswordHasher passwordHasher, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Unauthorized();
    }

    var normalizedEmail = request.Email.Trim().ToLowerInvariant();
    UserAccount? account;
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        account = await UserAuthDb.GetUserByEmailAsync(options.ConnectionString, normalizedEmail, ct);
    }
    else
    {
        store.UsersByEmail.TryGetValue(normalizedEmail, out account);
    }

    if (account is null || !passwordHasher.Verify(request.Password, account.PasswordHash))
    {
        return Results.Unauthorized();
    }

    var tokenPair = tokenService.CreateTokenPair(account.Profile);
    var refreshHash = HashToken(tokenPair.RefreshToken);
    var expiresAt = DateTimeOffset.UtcNow.AddDays(options.RefreshTokenDays);
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        await UserAuthDb.SaveRefreshTokenAsync(options.ConnectionString, account.Profile.UserId, refreshHash, expiresAt, ct);
    }
    else
    {
        store.RefreshTokens[refreshHash] = new RefreshTokenRecord(account.Profile.UserId, refreshHash, expiresAt, null, null);
    }

    return Results.Ok(new
    {
        accessToken = tokenPair.AccessToken,
        refreshToken = tokenPair.RefreshToken,
        accessTokenExpiresAt = tokenPair.AccessTokenExpiresAt,
        user = account.Profile
    });
});

app.MapPost("/users/refresh", async (RefreshTokenRequest request, AuthTokenService tokenService, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RefreshToken))
    {
        return Results.Unauthorized();
    }

    var presentedHash = HashToken(request.RefreshToken.Trim());
    RefreshTokenRecord? storedToken;
    UserAccount? account;
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        storedToken = await UserAuthDb.GetRefreshTokenAsync(options.ConnectionString, presentedHash, ct);
        if (storedToken is null || storedToken.RevokedAt is not null || storedToken.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Results.Unauthorized();
        }

        account = await UserAuthDb.GetUserByIdAsync(options.ConnectionString, storedToken.UserId, ct);
        if (account is null)
        {
            return Results.Unauthorized();
        }
    }
    else
    {
        if (!store.RefreshTokens.TryGetValue(presentedHash, out storedToken) ||
            storedToken.RevokedAt is not null ||
            storedToken.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Results.Unauthorized();
        }

        store.UsersById.TryGetValue(storedToken.UserId, out account);
        if (account is null)
        {
            return Results.Unauthorized();
        }
    }

    var nextTokenPair = tokenService.CreateTokenPair(account.Profile);
    var nextRefreshHash = HashToken(nextTokenPair.RefreshToken);
    var nextExpiresAt = DateTimeOffset.UtcNow.AddDays(options.RefreshTokenDays);
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        await UserAuthDb.RotateRefreshTokenAsync(options.ConnectionString, presentedHash, nextRefreshHash, nextExpiresAt, ct);
    }
    else
    {
        store.RefreshTokens[presentedHash] = storedToken with
        {
            RevokedAt = DateTimeOffset.UtcNow,
            ReplacedByTokenHash = nextRefreshHash
        };
        store.RefreshTokens[nextRefreshHash] = new RefreshTokenRecord(account.Profile.UserId, nextRefreshHash, nextExpiresAt, null, null);
    }

    return Results.Ok(new
    {
        accessToken = nextTokenPair.AccessToken,
        refreshToken = nextTokenPair.RefreshToken,
        accessTokenExpiresAt = nextTokenPair.AccessTokenExpiresAt,
        user = account.Profile
    });
});

app.MapPost("/users/logout", async (RefreshTokenRequest request, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.RefreshToken))
    {
        return Results.NoContent();
    }

    var refreshHash = HashToken(request.RefreshToken.Trim());
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        await UserAuthDb.RevokeRefreshTokenAsync(options.ConnectionString, refreshHash, ct);
    }
    else if (store.RefreshTokens.TryGetValue(refreshHash, out var existing))
    {
        store.RefreshTokens[refreshHash] = existing with { RevokedAt = DateTimeOffset.UtcNow };
    }

    return Results.NoContent();
});

app.MapGet("/users/{userId:guid}", async (Guid userId, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(options.ConnectionString))
    {
        var account = await UserAuthDb.GetUserByIdAsync(options.ConnectionString, userId, ct);
        return account is null ? Results.NotFound() : Results.Ok(account.Profile);
    }

    return store.UsersById.TryGetValue(userId, out var accountInMemory)
        ? Results.Ok(accountInMemory.Profile)
        : Results.NotFound();
});

app.Run();

static string HashToken(string token) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
