using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Saves one prepared message into the account's Drafts folder instead of
/// delivering it. The store IMAP-APPENDs the prepared MIME to the account's
/// Drafts folder (with the <c>\Draft</c> flag) and never delivers anything: the
/// human later reviews, possibly modifies, and finally sends the draft manually
/// from their own mail client. Ambiguous transport failures (an append whose
/// server-side outcome is unknown) must be reported as
/// <see cref="SendTransportOutcome.Indeterminate"/> so the send ledger can treat
/// them as terminal-and-unknown, exactly like SMTP deliveries.
/// </summary>
public interface IDraftMessageStore
{
	Task<SendTransportOutcome> SaveAsync(
		AccountProfile profile,
		PasswordCredentialLease credential,
		PreparedOutgoingMessage message,
		CancellationToken cancellationToken);
}
