namespace MailKit.Agent.Mail.Connections;

public sealed class CommandTimeoutScope : IDisposable
{
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _timeoutSource;
    private readonly CancellationTokenSource _linkedSource;

    private CommandTimeoutScope(TimeSpan timeout, CancellationToken callerToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _callerToken = callerToken;
        _timeoutSource = new CancellationTokenSource(timeout);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken, _timeoutSource.Token);
    }

    public CancellationToken Token => _linkedSource.Token;

    public bool IsTimeoutCancellation =>
        _timeoutSource.IsCancellationRequested && !_callerToken.IsCancellationRequested;

    public static CommandTimeoutScope Create(
        TimeSpan timeout,
        CancellationToken callerToken) =>
        new(timeout, callerToken);

    public void Dispose()
    {
        _linkedSource.Dispose();
        _timeoutSource.Dispose();
    }
}
