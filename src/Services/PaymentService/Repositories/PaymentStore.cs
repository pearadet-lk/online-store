using System.Collections.Concurrent;
using Contracts;

internal sealed class PaymentStore
{
    public ConcurrentDictionary<Guid, PaymentDto> PaymentsByOrderId { get; } = new();
    public ConcurrentDictionary<string, IdempotencyEntry> IdempotencyResponses { get; } = new();
}

internal sealed record IdempotencyEntry(string RequestHash, PaymentDto Payment);
