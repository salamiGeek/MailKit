using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// The terminal outcome of a single SMTP transport attempt, as reported by the
/// gateway. Only <see cref="SendState.Succeeded"/>, <see cref="SendState.Failed"/>,
/// and <see cref="SendState.Indeterminate"/> are valid outcome states;
/// <see cref="SendState.Indeterminate"/> means the server may or may not have
/// accepted the message and the send must never be retried with the same
/// idempotency key.
/// </summary>
public sealed record SendTransportOutcome
{
    public SendState State { get; }
    public ToolError? Error { get; }

    private SendTransportOutcome(SendState state, ToolError? error)
    {
        if (state is not (SendState.Succeeded or SendState.Failed or SendState.Indeterminate))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (state == SendState.Failed && error is null)
            throw new ArgumentNullException(nameof(error), "Failed outcomes require an error.");

        State = state;
        Error = error;
    }

    public static SendTransportOutcome Succeeded() =>
        new(SendState.Succeeded, null);

    public static SendTransportOutcome Failed(ToolError error) =>
        new(SendState.Failed, error ?? throw new ArgumentNullException(nameof(error)));

    public static SendTransportOutcome Indeterminate(ToolError? error = null) =>
        new(SendState.Indeterminate, error);
}
