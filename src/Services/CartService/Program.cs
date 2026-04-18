using System.Collections.Concurrent;
using Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<CartStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { service = "cart-service", status = "ok", recommendation = "Use Redis in production" }));

app.MapGet("/carts/{userId:guid}", (Guid userId, CartStore store) =>
{
    if (!store.CartByUserId.TryGetValue(userId, out var cart))
    {
        return Results.Ok(new CartDto(Guid.NewGuid(), userId, Array.Empty<CartItemDto>(), DateTimeOffset.UtcNow));
    }

    return Results.Ok(cart);
});

app.MapPut("/carts/{userId:guid}", (Guid userId, UpsertCartRequest request, CartStore store) =>
{
    if (userId != request.UserId)
    {
        return Results.BadRequest(new { error = "Route userId must match request userId." });
    }

    var existingCartId = store.CartByUserId.TryGetValue(userId, out var existing)
        ? existing.CartId
        : Guid.NewGuid();

    var cart = new CartDto(existingCartId, userId, request.Items, DateTimeOffset.UtcNow);
    store.CartByUserId[userId] = cart;
    return Results.Ok(cart);
});

app.MapDelete("/carts/{userId:guid}", (Guid userId, CartStore store) =>
{
    store.CartByUserId.TryRemove(userId, out _);
    return Results.NoContent();
});

app.Run();

internal sealed class CartStore
{
    public ConcurrentDictionary<Guid, CartDto> CartByUserId { get; } = new();
}
