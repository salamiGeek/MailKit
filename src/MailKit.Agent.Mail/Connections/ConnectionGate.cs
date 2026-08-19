using System.Collections.Concurrent;

namespace MailKit.Agent.Mail.Connections;

public sealed class ConnectionGate : IDisposable
{
    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<(string AccountId, string Protocol), SemaphoreSlim> _keyed = new();
    private readonly int _maxPerAccountProtocol;
    private int _disposed;

    public ConnectionGate(ConnectionLimits? limits = null)
    {
        limits ??= ConnectionLimits.Default;
        _global = new SemaphoreSlim(limits.MaxGlobal, limits.MaxGlobal);
        _maxPerAccountProtocol = limits.MaxPerAccountProtocol;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string accountId,
        string protocol,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);

        await _global.WaitAsync(cancellationToken).ConfigureAwait(false);
        var globalHeld = true;
        SemaphoreSlim? keyed = null;
        try
        {
            keyed = _keyed.GetOrAdd(
                (accountId, protocol),
                _ => new SemaphoreSlim(_maxPerAccountProtocol, _maxPerAccountProtocol));
            await keyed.WaitAsync(cancellationToken).ConfigureAwait(false);
            globalHeld = false;
            return new Lease(_global, keyed);
        }
        finally
        {
            if (globalHeld)
                _global.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _global.Dispose();
        foreach (var semaphore in _keyed.Values)
            semaphore.Dispose();
    }

    private sealed class Lease(
        SemaphoreSlim global,
        SemaphoreSlim keyed) : IAsyncDisposable
    {
        private SemaphoreSlim? _global = global;
        private SemaphoreSlim? _keyed = keyed;

        public ValueTask DisposeAsync()
        {
            var keyedSemaphore = Interlocked.Exchange(ref _keyed, null);
            var globalSemaphore = Interlocked.Exchange(ref _global, null);
            if (keyedSemaphore is not null)
                keyedSemaphore.Release();
            if (globalSemaphore is not null)
                globalSemaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
