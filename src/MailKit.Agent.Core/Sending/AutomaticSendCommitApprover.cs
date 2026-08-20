// DEBUG-only by design: this type is compiled out of Release binaries entirely so
// production hosts cannot resolve it even by accident. Its sole reference
// (TestGatewayRegistration) is likewise DEBUG-gated and additionally requires
// MAILKIT_AGENT_TEST_MODE=1, which Release builds reject outright.
#if DEBUG
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
	public ValueTask<SendApprovalOutcome> ApproveAsync(
		SendPreview preview, CancellationToken cancellationToken) =>
		ValueTask.FromResult(SendApprovalOutcome.Approved);
}
#endif
