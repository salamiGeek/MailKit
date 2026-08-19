using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Net.Imap;

namespace MailKit.Agent.Mail.Imap;

public interface IImapClientFactory
{
    Task<ImapClient> CreateAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken);
}
