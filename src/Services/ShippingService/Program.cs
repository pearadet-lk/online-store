using System.Collections.Concurrent;
using Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<ShipmentStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { service = "shipping-service", status = "ok" }));

app.MapPost("/shipments/{orderId:guid}", (Guid orderId, ShipmentStore store) =>
{
    var shipment = new ShipmentDto(
        Guid.NewGuid(),
        orderId,
        "DHL",
        $"TRK-{Guid.NewGuid():N}"[..16],
        "Dispatched",
        DateTimeOffset.UtcNow);
    store.ShipmentsByOrderId[orderId] = shipment;
    return Results.Created($"/shipments/{orderId}", shipment);
});

app.MapGet("/shipments/{orderId:guid}", (Guid orderId, ShipmentStore store) =>
{
    return store.ShipmentsByOrderId.TryGetValue(orderId, out var shipment)
        ? Results.Ok(shipment)
        : Results.NotFound();
});

app.Run();

internal sealed class ShipmentStore
{
    public ConcurrentDictionary<Guid, ShipmentDto> ShipmentsByOrderId { get; } = new();
}
