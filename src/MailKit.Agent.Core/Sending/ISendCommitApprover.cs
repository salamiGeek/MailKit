namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Local human-approval gate for the send commit phase. <see cref="SendApplication"/>
/// calls <see cref="ApproveAsync"/> with the exact prepared preview after the cheap
/// confirmation-token validations (expiry, session, account, content hash) but
/// BEFORE the one-time token is consumed, the ledger is written, or SMTP is
/// touched. The approval factor is something the MCP caller cannot produce on its
/// own, so an agent cannot chain prepare and commit in one unattended run.
/// </summary>
public interface ISendCommitApprover
{
	/// <summary>
	/// Asks the local human whether this prepared send may be delivered. Returns
	/// true only when the local approval factor was granted; false means the commit
	/// must fail with <c>send.approval_declined</c> without consuming the token.
	/// The preview is secret-free by construction, so implementations may render it
	/// locally but must never transmit it or display the confirmation token it carries.
	/// </summary>
	ValueTask<bool> ApproveAsync(SendPreview preview, CancellationToken cancellationToken);
}
