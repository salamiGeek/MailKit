namespace MailKit.Agent.Mail.Connections;

internal sealed class FailedServiceCleanupOwner
{
    private readonly Dictionary<long, OwnedCleanup> _activeCleanups = [];
    private readonly Action<Exception>? _failureObserver;
    private readonly object _syncRoot = new();
    private TaskCompletionSource? _idleSignal;
    private long _nextId;

    public FailedServiceCleanupOwner(Action<Exception>? failureObserver = null)
    {
        _failureObserver = failureObserver;
    }

    public int ActiveCleanupCount
    {
        get
        {
            lock (_syncRoot)
                return _activeCleanups.Count;
        }
    }

    public Task WhenIdleAsync()
    {
        lock (_syncRoot)
        {
            return _activeCleanups.Count == 0
                ? Task.CompletedTask
                : _idleSignal!.Task;
        }
    }

    public void Own(IMailService service, Task disconnectTask)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(disconnectTask);

        long id;
        lock (_syncRoot)
        {
            if (_activeCleanups.Count == 0)
            {
                _idleSignal = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            id = ++_nextId;
            _activeCleanups.Add(id, new OwnedCleanup(service, disconnectTask));
        }

        disconnectTask.ConfigureAwait(false).GetAwaiter().OnCompleted(
            () => Complete(id, disconnectTask));
    }

    private void Complete(long id, Task disconnectTask)
    {
        try
        {
            disconnectTask.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            try
            {
                _failureObserver?.Invoke(exception);
            }
            catch
            {
                // Diagnostics must not escape the owned cleanup lifecycle.
            }
        }

        TaskCompletionSource? idleSignal = null;
        lock (_syncRoot)
        {
            _activeCleanups.Remove(id);
            if (_activeCleanups.Count == 0)
            {
                idleSignal = _idleSignal;
                _idleSignal = null;
            }
        }

        idleSignal?.TrySetResult();
    }

    private sealed record OwnedCleanup(IMailService Service, Task DisconnectTask);
}
