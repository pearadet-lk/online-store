internal static class InventoryOperations
{
    public static (bool Success, string? ErrorCode) TryCheck(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        return item.AvailableQty >= quantity
            ? (true, null)
            : (false, "invalid");
    }

    public static (bool Success, string? ErrorCode) TryReserve(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        if (item.AvailableQty < quantity)
        {
            return (false, "invalid");
        }

        store.Items[productId] = item with
        {
            AvailableQty = item.AvailableQty - quantity,
            ReservedQty = item.ReservedQty + quantity,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (true, null);
    }

    public static (bool Success, string? ErrorCode) TryRelease(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        if (item.ReservedQty < quantity)
        {
            return (false, "invalid");
        }

        store.Items[productId] = item with
        {
            AvailableQty = item.AvailableQty + quantity,
            ReservedQty = item.ReservedQty - quantity,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (true, null);
    }

    public static (bool Success, string? ErrorCode) TryCommit(InventoryStore store, Guid productId, int quantity)
    {
        if (quantity <= 0)
        {
            return (false, "invalid");
        }

        if (!store.Items.TryGetValue(productId, out var item))
        {
            return (false, "not_found");
        }

        if (item.ReservedQty < quantity)
        {
            return (false, "invalid");
        }

        store.Items[productId] = item with
        {
            ReservedQty = item.ReservedQty - quantity,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (true, null);
    }
}
