using System.Collections.Concurrent;
using Contracts;

internal sealed class CartStore
{
    public ConcurrentDictionary<Guid, CartDto> CartByUserId { get; } = new();
}
