namespace MailKit.Agent.Core.Sending;

/// <summary>
/// The result of the local human-approval gate for one send commit.
/// </summary>
public enum SendApprovalOutcome
{
	/// <summary>
	/// The local human explicitly approved this exact preview; the commit may
	/// proceed to consume the one-time token and deliver.
	/// </summary>
	Approved,

	/// <summary>
	/// A local human was asked and said no, or the approval wait was cancelled by
	/// the caller. The commit fails with <c>send.approval_declined</c> without
	/// consuming the token.
	/// </summary>
	Declined,

	/// <summary>
	/// No local human could be asked at all (non-Windows host, missing input
	/// desktop, headless session). This is an environment fact, NOT a human
	/// refusal: the commit fails with <c>send.approval_unavailable</c> without
	/// consuming the token, and the operator should retry from an interactive
	/// session rather than interpret it as rejection.
	/// </summary>
	Unavailable
}
