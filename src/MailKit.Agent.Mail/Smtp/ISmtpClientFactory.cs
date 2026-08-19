using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Net.Smtp;

namespace MailKit.Agent.Mail.Smtp;

/// <summary>
/// Creates authenticated, connected SMTP clients for an account. Implementations are
/// expected to go through the shared secure connector so TLS policy, timeouts, and
/// sanitized error mapping stay uniform across protocols.
/// </summary>
public interface ISmtpClientFactory
{
    Task<SmtpClient> CreateAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken);
}
