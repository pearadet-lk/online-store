using System.Collections.Concurrent;
using Contracts;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<HistoryStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", () => Results.Ok(new { service = "history-service", status = "ok" }));

app.MapPost("/history/events", (OrderHistoryEventDto request, HistoryStore store) =>
{
    var history = request with
    {
        HistoryId = request.HistoryId == Guid.Empty ? Guid.NewGuid() : request.HistoryId,
        CreatedAt = request.CreatedAt == default ? DateTimeOffset.UtcNow : request.CreatedAt
    };

    store.EventsByUserId.AddOrUpdate(
        history.UserId,
        _ => new List<OrderHistoryEventDto> { history },
        (_, existing) =>
        {
            existing.Add(history);
            return existing;
        });

    return Results.Created($"/history/users/{history.UserId}", history);
});

app.MapGet("/history/users/{userId:guid}", (Guid userId, HistoryStore store) =>
{
    return store.EventsByUserId.TryGetValue(userId, out var events)
        ? Results.Ok(events.OrderByDescending(x => x.CreatedAt))
        : Results.Ok(Array.Empty<OrderHistoryEventDto>());
});

app.Run();

internal sealed class HistoryStore
{
    public ConcurrentDictionary<Guid, List<OrderHistoryEventDto>> EventsByUserId { get; } = new();
}
