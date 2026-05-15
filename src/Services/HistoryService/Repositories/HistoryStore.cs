using System.Collections.Concurrent;
using Contracts;

internal sealed class HistoryStore
{
    public ConcurrentDictionary<Guid, List<OrderHistoryEventDto>> EventsByUserId { get; } = new();
}
