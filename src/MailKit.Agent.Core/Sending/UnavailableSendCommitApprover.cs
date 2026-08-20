namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Reports local human approval as unavailable for every send commit. Production
/// hosts on platforms without an interactive desktop (non-Windows, or Windows
/// where the runtime environment has no input desktop) register this approver so
/// sends fail closed with the stable <c>send.approval_unavailable</c> error — an
/// environment fact distinct from a human refusal — instead of silently skipping
/// approval or hanging on a dialog nobody can see.
/// </summary>
public sealed class UnavailableSendCommitApprover : ISendCommitApprover
{
	/// <inheritdoc />
	public ValueTask<SendApprovalOutcome> ApproveAsync(
		SendPreview preview, CancellationToken cancellationToken) =>
		ValueTask.FromResult(SendApprovalOutcome.Unavailable);
}
