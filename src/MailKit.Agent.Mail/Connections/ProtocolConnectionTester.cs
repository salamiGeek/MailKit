using System.Text;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;

namespace MailKit.Agent.Mail.Connections;

public sealed class ProtocolConnectionTester : IProtocolConnectionTester
{
    private readonly Func<string, AccountProfile, PasswordCredentialLease,
        CancellationToken, Task<IMailService>> connect;
    private readonly Func<IMailService, IReadOnlyList<string>> getCapabilities;
    private readonly TimeSpan cleanupTimeout;

    public ProtocolConnectionTester()
        : this(new MailServiceConnector())
    {
    }

    public ProtocolConnectionTester(MailServiceConnector connector)
        : this(
            (protocol, profile, credential, cancellationToken) =>
                connector.ConnectAndAuthenticateAsync(
                    protocol,
                    GetRequiredEndpoint(profile, protocol),
                    profile.Username,
                    credential,
                    cancellationToken),
            GetCapabilities)
    {
        ArgumentNullException.ThrowIfNull(connector);
    }

    internal ProtocolConnectionTester(
        Func<string, AccountProfile, PasswordCredentialLease,
            CancellationToken, Task<IMailService>> connect,
        Func<IMailService, IReadOnlyList<string>> getCapabilities,
        TimeSpan? cleanupTimeout = null)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.getCapabilities = getCapabilities ?? throw new ArgumentNullException(nameof(getCapabilities));
        this.cleanupTimeout = cleanupTimeout ?? ConnectionLimits.Default.CommandTimeout;
        if (this.cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
    }

    public async Task<ProtocolConnectionResult> TestAsync(
        string protocol,
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        string normalizedProtocol = NormalizeProtocol(protocol);
        _ = GetRequiredEndpoint(profile, normalizedProtocol);

        IMailService? service = null;
        try
        {
            service = await connect(
                normalizedProtocol, profile, credential, cancellationToken).ConfigureAwait(false);
            return new ProtocolConnectionResult(
                normalizedProtocol,
                service.IsConnected,
                service.IsSecure,
                service.IsAuthenticated,
                getCapabilities(service),
                null);
        }
        finally
        {
            if (service is not null)
                await CleanupAsync(service).ConfigureAwait(false);
        }
    }

    private async Task CleanupAsync(IMailService service)
    {
        try
        {
            if (service.IsConnected)
            {
                using var scope = CommandTimeoutScope.Create(
                    cleanupTimeout, CancellationToken.None);
                await service.DisconnectAsync(true, scope.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // A cleanup failure must not replace the completed connection test result.
        }
        finally
        {
            try
            {
                service.Dispose();
            }
            catch
            {
                // A cleanup failure must not replace the completed connection test result.
            }
        }
    }

    private static EndpointSettings GetRequiredEndpoint(AccountProfile profile, string protocol) =>
        protocol switch
        {
            "imap" when profile.Imap is not null => profile.Imap,
            "pop3" when profile.Pop3 is not null => profile.Pop3,
            "smtp" when profile.Smtp is not null => profile.Smtp,
            "imap" or "pop3" or "smtp" => throw new MailOperationException(new ToolError(
                $"{protocol}.not_configured",
                ErrorCategory.Capability,
                $"{protocol.ToUpperInvariant()} is not configured for this account.",
                false,
                null,
                new Dictionary<string, string> { ["protocol"] = protocol })),
            _ => throw ProtocolError()
        };

    private static string NormalizeProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
            throw ProtocolError();
        string normalized = protocol.ToLowerInvariant();
        return normalized is "imap" or "pop3" or "smtp"
            ? normalized
            : throw ProtocolError();
    }

    private static IReadOnlyList<string> GetCapabilities(IMailService service)
    {
        string value = service switch
        {
            ImapClient client => client.Capabilities.ToString(),
            Pop3Client client => client.Capabilities.ToString(),
            SmtpClient client => client.Capabilities.ToString(),
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "None", StringComparison.Ordinal))
            return Array.Empty<string>();

        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ToSnakeCase)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ToSnakeCase(string value)
    {
        var output = new StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character) &&
                (char.IsLower(value[index - 1]) ||
                 index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                output.Append('_');
            }
            output.Append(char.ToLowerInvariant(character));
        }
        return output.ToString();
    }

    private static MailOperationException ProtocolError() => new(new ToolError(
        "connection.protocol_error",
        ErrorCategory.Capability,
        "The requested mail protocol is not supported.",
        false,
        null,
        null));
}
