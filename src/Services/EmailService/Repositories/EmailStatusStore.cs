using System.Collections.Concurrent;
using Contracts;

internal sealed class EmailStatusStore
{
    public ConcurrentDictionary<Guid, EmailSendStatusDto> StatusByOrderId { get; } = new();
}
