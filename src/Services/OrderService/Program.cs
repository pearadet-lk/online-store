using System.Collections.Concurrent;
using Contracts;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();
    builder.Services.AddOpenApi();
    builder.Services.AddSingleton<OrderStore>();

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
    }

    app.UseSerilogRequestLogging();

    app.MapGet("/health", () => Results.Ok(new { service = "order-service", status = "ok" }));

    app.MapPost("/orders", (CreateOrderRequest request, OrderStore store) =>
    {
        var totalAmount = request.Items.Sum(x => x.Quantity * x.UnitPrice);
        var order = new OrderDto(
            Guid.NewGuid(),
            request.UserId,
            totalAmount,
            request.Currency.ToUpperInvariant(),
            "Pending",
            DateTimeOffset.UtcNow);

        store.Orders[order.OrderId] = order;
        return Results.Created($"/orders/{order.OrderId}", order);
    });

    app.MapGet("/orders/{orderId:guid}", (Guid orderId, OrderStore store) =>
    {
        return store.Orders.TryGetValue(orderId, out var order)
            ? Results.Ok(order)
            : Results.NotFound();
    });

    app.MapPost("/orders/{orderId:guid}/complete", (Guid orderId, OrderStore store) =>
    {
        if (!store.Orders.TryGetValue(orderId, out var order))
        {
            return Results.NotFound();
        }

        store.Orders[orderId] = order with { Status = "Completed" };
        return Results.Ok(store.Orders[orderId]);
    });

    app.MapPost("/orders/{orderId:guid}/fail", (Guid orderId, OrderStore store) =>
    {
        if (!store.Orders.TryGetValue(orderId, out var order))
        {
            return Results.NotFound();
        }

        store.Orders[orderId] = order with { Status = "Failed" };
        return Results.Ok(store.Orders[orderId]);
    });

    app.Run();
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "Order service host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

internal sealed class OrderStore
{
    public ConcurrentDictionary<Guid, OrderDto> Orders { get; } = new();
}
