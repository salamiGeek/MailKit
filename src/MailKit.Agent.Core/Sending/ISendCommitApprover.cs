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
	/// Asks the local human whether this prepared send may be delivered, returning
	/// a <see cref="SendApprovalOutcome"/> that distinguishes an explicit human
	/// refusal or caller cancellation (<see cref="SendApprovalOutcome.Declined">Declined</see>)
	/// from an environment where no human can be asked at all
	/// (<see cref="SendApprovalOutcome.Unavailable">Unavailable</see>). Either
	/// non-approved outcome fails the commit without consuming the token. The
	/// preview is secret-free by construction, so implementations may render it
	/// locally but must never transmit it or display the confirmation token it
	/// carries.
	/// </summary>
	ValueTask<SendApprovalOutcome> ApproveAsync(SendPreview preview, CancellationToken cancellationToken);
}
