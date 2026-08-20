namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Declines every send commit. Production hosts on platforms without an
/// interactive desktop (non-Windows, or anywhere no local human can be asked)
/// register this approver so sends fail closed with the stable
/// <c>send.approval_declined</c> error instead of silently skipping approval.
/// </summary>
public sealed class UnavailableSendCommitApprover : ISendCommitApprover
{
	/// <inheritdoc />
	public ValueTask<bool> ApproveAsync(SendPreview preview, CancellationToken cancellationToken) =>
		ValueTask.FromResult(false);
}
