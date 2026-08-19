using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Mail.Connections;
using MailKit.Net.Imap;

namespace MailKit.Agent.Mail.Imap;

public sealed class ImapClientFactory : IImapClientFactory
{
    private readonly MailServiceConnector connector;

    public ImapClientFactory()
        : this(new MailServiceConnector())
    {
    }

    public ImapClientFactory(MailServiceConnector connector)
    {
        this.connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async Task<ImapClient> CreateAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        EndpointSettings endpoint = profile.Imap ?? throw new MailOperationException(new ToolError(
            "imap.not_configured",
            ErrorCategory.Capability,
            "IMAP is not configured for this account.",
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "imap" }));

        var service = await connector.ConnectAndAuthenticateAsync(
            "imap", endpoint, profile.Username, credential, cancellationToken).ConfigureAwait(false);

        if (service is ImapClient client)
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
                ["protocol"] = "imap",
                ["operation"] = "connect"
            }));
    }
}
