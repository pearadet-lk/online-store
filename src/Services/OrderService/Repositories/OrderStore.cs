using System.Collections.Concurrent;
using Contracts;

internal sealed class OrderStore
{
    public ConcurrentDictionary<Guid, OrderEntry> Orders { get; } = new();
}

internal sealed record OrderEntry(OrderDto Order, IReadOnlyList<CartItemDto> Items);
