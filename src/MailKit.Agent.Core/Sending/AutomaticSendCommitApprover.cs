namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Approves every send commit unconditionally. This implementation exists for
/// DEBUG test fixtures only (stdio process tests under
/// <c>MAILKIT_AGENT_TEST_MODE=1</c>) and must never be registered by production
/// hosts: it removes the local human-approval gate entirely, which is the exact
/// chaining abuse the gate exists to stop.
/// </summary>
public sealed class AutomaticSendCommitApprover : ISendCommitApprover
{
	/// <inheritdoc />
	public ValueTask<bool> ApproveAsync(SendPreview preview, CancellationToken cancellationToken) =>
		ValueTask.FromResult(true);
}
