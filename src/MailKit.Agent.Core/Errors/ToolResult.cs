using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Errors;

public sealed record ToolResult<T>(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("error")] ToolError? Error,
    [property: JsonPropertyName("correlation_id")] string CorrelationId)
{
    public static ToolResult<T> Success(T data, string correlationId) =>
        new(true, data, null, correlationId);

    public static ToolResult<T> Failure(ToolError error, string correlationId) =>
        new(false, default, error, correlationId);
}
