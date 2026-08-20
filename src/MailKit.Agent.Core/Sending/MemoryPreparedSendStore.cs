using System.Security.Cryptography;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// In-memory prepared-send store. <see cref="TakeAsync"/> removes a preparation
/// atomically so a one-time confirmation token can only ever be consumed once,
/// while <see cref="TryGetAsync"/> peeks without consuming. Expired preparations
/// are swept on access and their MIME byte arrays are zeroed.
/// </summary>
public sealed class MemoryPreparedSendStore : IPreparedSendStore, IDisposable
{
    private readonly Dictionary<string, PreparedOutgoingMessage> items =
        new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private bool disposed;

    public MemoryPreparedSendStore(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task AddAsync(PreparedOutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            SweepExpired_unlocked();
            items[message.PreparationId] = message;
        }

        return Task.CompletedTask;
    }

    public Task<PreparedOutgoingMessage?> TryGetAsync(
        string preparationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PreparedOutgoingMessage? peeked;
        lock (gate)
        {
            SweepExpired_unlocked();
            peeked = items.TryGetValue(preparationId, out var message) ? message : null;
        }

        return Task.FromResult(peeked);
    }

    public Task<PreparedOutgoingMessage?> TakeAsync(
        string preparationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PreparedOutgoingMessage? taken;
        lock (gate)
        {
            SweepExpired_unlocked();
            if (items.Remove(preparationId, out taken) && taken.ExpiresAt <= timeProvider.GetUtcNow())
            {
                Zero(taken);
                taken = null;
            }
        }

        return Task.FromResult(taken);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            foreach (var message in items.Values)
                Zero(message);
            items.Clear();
            disposed = true;
        }
    }

    private void SweepExpired_unlocked()
    {
        var now = timeProvider.GetUtcNow();
        List<string>? expired = null;
        foreach (var (id, message) in items)
        {
            if (message.ExpiresAt <= now)
                (expired ??= new List<string>()).Add(id);
        }

        if (expired is null)
            return;

        foreach (var id in expired)
        {
            if (items.Remove(id, out var message))
                Zero(message);
        }
    }

    private static void Zero(PreparedOutgoingMessage message) =>
        CryptographicOperations.ZeroMemory(message.MimeMessage);
}
