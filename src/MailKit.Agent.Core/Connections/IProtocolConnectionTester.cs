using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Core.Connections;

public interface IProtocolConnectionTester
{
    Task<ProtocolConnectionResult> TestAsync(
        string protocol,
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken);
}
