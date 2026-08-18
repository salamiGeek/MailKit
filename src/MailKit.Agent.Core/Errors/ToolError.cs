using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Errors;

public sealed record ToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] ErrorCategory Category,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("retry_after")] DateTimeOffset? RetryAfter,
    [property: JsonPropertyName("details")] IReadOnlyDictionary<string, string>? Details);
