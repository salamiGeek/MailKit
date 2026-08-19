using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Mail.Connections;
using MailKit.Net.Smtp;

namespace MailKit.Agent.Mail.Smtp;

/// <summary>
/// Opens SMTP sessions through the shared <see cref="MailServiceConnector"/>, which
/// enforces implicit/start TLS only, connect/authenticate timeouts, and sanitized
/// failure cleanup before the returned client is used.
/// </summary>
public sealed class SmtpClientFactory : ISmtpClientFactory
{
    private readonly MailServiceConnector connector;

    public SmtpClientFactory()
        : this(new MailServiceConnector())
    {
    }

    public SmtpClientFactory(MailServiceConnector connector)
    {
        this.connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async Task<SmtpClient> CreateAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        EndpointSettings endpoint = profile.Smtp ?? throw new MailOperationException(new ToolError(
            "smtp.not_configured",
            ErrorCategory.Capability,
            "SMTP is not configured for this account.",
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "smtp" }));

        var service = await connector.ConnectAndAuthenticateAsync(
            "smtp", endpoint, profile.Username, credential, cancellationToken).ConfigureAwait(false);

        if (service is SmtpClient client)
            return client;

        service.Dispose();
        throw new MailOperationException(new ToolError(
            "connection.internal",
            ErrorCategory.Internal,
            "The mail operation failed.",
            false,
            null,
            new Dictionary<string, string>
            {
                ["protocol"] = "smtp",
                ["operation"] = "connect"
            }));
    }
}
