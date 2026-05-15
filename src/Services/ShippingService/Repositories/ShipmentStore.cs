using System.Collections.Concurrent;
using Contracts;

internal sealed class ShipmentStore
{
    public ConcurrentDictionary<Guid, ShipmentDto> ShipmentsByOrderId { get; } = new();
}
