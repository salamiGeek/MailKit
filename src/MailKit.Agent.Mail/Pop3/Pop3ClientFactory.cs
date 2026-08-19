using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Mail.Connections;
using MailKit.Net.Pop3;

namespace MailKit.Agent.Mail.Pop3;

public sealed class Pop3ClientFactory : IPop3ClientFactory
{
    private readonly MailServiceConnector connector;

    public Pop3ClientFactory()
        : this(new MailServiceConnector())
    {
    }

    public Pop3ClientFactory(MailServiceConnector connector)
    {
        this.connector = connector ?? throw new ArgumentNullException(nameof(connector));
    }

    public async Task<Pop3Client> CreateAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        EndpointSettings endpoint = profile.Pop3 ?? throw new MailOperationException(new ToolError(
            "pop3.not_configured",
            ErrorCategory.Capability,
            "POP3 is not configured for this account.",
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "pop3" }));

        var service = await connector.ConnectAndAuthenticateAsync(
            "pop3", endpoint, profile.Username, credential, cancellationToken).ConfigureAwait(false);

        if (service is Pop3Client client)
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
                ["protocol"] = "pop3",
                ["operation"] = "connect"
            }));
    }
}
