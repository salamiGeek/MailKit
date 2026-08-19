namespace MailKit.Agent.Mail.Connections;

public sealed record ConnectionLimits
{
    private static readonly TimeSpan MaxConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxAuthenticateTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxCommandTimeout = TimeSpan.FromSeconds(30);
    private const int MaxPerAccountProtocolLimit = 2;
    private const int MaxGlobalLimit = 8;

    public ConnectionLimits(
        TimeSpan connectTimeout,
        TimeSpan authenticateTimeout,
        TimeSpan commandTimeout,
        int maxPerAccountProtocol,
        int maxGlobal)
    {
        ConnectTimeout = ValidateTimeout(
            connectTimeout, MaxConnectTimeout, nameof(connectTimeout));
        AuthenticateTimeout = ValidateTimeout(
            authenticateTimeout, MaxAuthenticateTimeout, nameof(authenticateTimeout));
        CommandTimeout = ValidateTimeout(
            commandTimeout, MaxCommandTimeout, nameof(commandTimeout));
        MaxPerAccountProtocol = ValidateCount(
            maxPerAccountProtocol, MaxPerAccountProtocolLimit, nameof(maxPerAccountProtocol));
        MaxGlobal = ValidateCount(maxGlobal, MaxGlobalLimit, nameof(maxGlobal));
    }

    public TimeSpan ConnectTimeout { get; }

    public TimeSpan AuthenticateTimeout { get; }

    public TimeSpan CommandTimeout { get; }

    public int MaxPerAccountProtocol { get; }

    public int MaxGlobal { get; }

    public static ConnectionLimits Default { get; } = new(
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30), 2, 8);

    private static TimeSpan ValidateTimeout(
        TimeSpan value, TimeSpan maximum, string parameterName)
    {
        if (value <= TimeSpan.Zero || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName);

        return value;
    }

    private static int ValidateCount(int value, int maximum, string parameterName)
    {
        if (value < 1 || value > maximum)
            throw new ArgumentOutOfRangeException(parameterName);

        return value;
    }
}
