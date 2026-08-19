using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Net.Pop3;

namespace MailKit.Agent.Mail.Pop3;

public interface IPop3ClientFactory
{
    Task<Pop3Client> CreateAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken);
}
