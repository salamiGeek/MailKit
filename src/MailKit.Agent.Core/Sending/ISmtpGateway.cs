using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Sends exactly one prepared message over SMTP. The gateway receives the full
/// <see cref="PreparedOutgoingMessage"/> — the MIME bytes for the DATA payload plus
/// the envelope metadata — rather than raw MIME bytes alone, because blind-copy
/// recipients must be delivered through the SMTP envelope (RCPT TO) while the
/// serialized MIME payload must never contain Bcc headers.
/// </summary>
public interface ISmtpGateway
{
    Task<SendTransportOutcome> SendAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        PreparedOutgoingMessage message,
        CancellationToken cancellationToken);
}
