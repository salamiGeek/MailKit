using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Policy;

public sealed record PolicyDecision(
    bool Allowed,
    bool ConfirmationRequired,
    ToolError? Error);
