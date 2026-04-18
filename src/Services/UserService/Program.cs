using System.Collections.Concurrent;
using Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<UserStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { service = "user-service", status = "ok" }));

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
