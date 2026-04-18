using System.Collections.Concurrent;
using Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<InventoryStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { service = "inventory-service", status = "ok" }));

app.MapGet("/inventory/{productId:guid}", (Guid productId, InventoryStore store) =>
{
    return store.Items.TryGetValue(productId, out var item)
        ? Results.Ok(item)
        : Results.NotFound();
});

app.MapPut("/inventory/{productId:guid}", (Guid productId, int availableQty, InventoryStore store) =>
{
    var existingReservedQty = store.Items.TryGetValue(productId, out var existing) ? existing.ReservedQty : 0;
    var updated = new InventoryItemDto(productId, Math.Max(0, availableQty), existingReservedQty, DateTimeOffset.UtcNow);
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.MapPost("/inventory/{productId:guid}/reserve", (Guid productId, int quantity, InventoryStore store) =>
{
    if (!store.Items.TryGetValue(productId, out var item))
    {
        return Results.NotFound();
    }

    if (quantity <= 0 || item.AvailableQty < quantity)
    {
        return Results.BadRequest(new { error = "Insufficient stock." });
    }

    var updated = item with
    {
        AvailableQty = item.AvailableQty - quantity,
        ReservedQty = item.ReservedQty + quantity,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.MapPost("/inventory/{productId:guid}/release", (Guid productId, int quantity, InventoryStore store) =>
{
    if (quantity <= 0)
    {
        return Results.BadRequest(new { error = "Quantity must be positive." });
    }

    if (!store.Items.TryGetValue(productId, out var item))
    {
        return Results.NotFound();
    }

    if (item.ReservedQty < quantity)
    {
        return Results.BadRequest(new { error = "Cannot release more than reserved quantity." });
    }

    var updated = item with
    {
        AvailableQty = item.AvailableQty + quantity,
        ReservedQty = item.ReservedQty - quantity,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.MapPost("/inventory/{productId:guid}/commit", (Guid productId, int quantity, InventoryStore store) =>
{
    if (quantity <= 0)
    {
        return Results.BadRequest(new { error = "Quantity must be positive." });
    }

    if (!store.Items.TryGetValue(productId, out var item))
    {
        return Results.NotFound();
    }

    if (item.ReservedQty < quantity)
    {
        return Results.BadRequest(new { error = "Cannot commit more than reserved quantity." });
    }

    var updated = item with
    {
        ReservedQty = item.ReservedQty - quantity,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    store.Items[productId] = updated;
    return Results.Ok(updated);
});

app.Run();

internal sealed class InventoryStore
{
    public ConcurrentDictionary<Guid, InventoryItemDto> Items { get; } = new(
        new[]
        {
            new KeyValuePair<Guid, InventoryItemDto>(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                new InventoryItemDto(Guid.Parse("11111111-1111-1111-1111-111111111111"), 100, 0, DateTimeOffset.UtcNow)),
            new KeyValuePair<Guid, InventoryItemDto>(
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                new InventoryItemDto(Guid.Parse("22222222-2222-2222-2222-222222222222"), 100, 0, DateTimeOffset.UtcNow))
        });
}
