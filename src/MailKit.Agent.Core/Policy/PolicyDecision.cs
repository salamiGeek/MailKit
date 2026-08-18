using System.Text.Json.Serialization;
using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Policy;

public sealed record PolicyDecision(
    [property: JsonPropertyName("allowed")] bool Allowed,
    [property: JsonPropertyName("confirmation_required")] bool ConfirmationRequired,
    [property: JsonPropertyName("error")] ToolError? Error);
