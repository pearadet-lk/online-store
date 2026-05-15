using System.Collections.Concurrent;
using Contracts;

internal sealed class InventoryStore
{
    private static readonly DateTimeOffset SeedTime = DateTimeOffset.UtcNow;

    public ConcurrentDictionary<Guid, InventoryItemDto> Items { get; } = new(
        CatalogSeed.DefaultProducts().ToDictionary(
            p => p.ProductId,
            p => new InventoryItemDto(p.ProductId, 100, 0, SeedTime)));
}
