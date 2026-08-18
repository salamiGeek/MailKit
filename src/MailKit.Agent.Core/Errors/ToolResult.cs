using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Errors;

public sealed record ToolResult<T>
{
    [JsonPropertyName("ok")]
    public bool Ok { get; }

    [JsonPropertyName("data")]
    public T? Data { get; }

    [JsonPropertyName("error")]
    public ToolError? Error { get; }

    [JsonPropertyName("correlation_id")]
    public string CorrelationId { get; }

    private ToolResult(bool ok, T? data, ToolError? error, string correlationId)
    {
        Ok = ok;
        Data = data;
        Error = error;
        CorrelationId = correlationId;
    }

    public static ToolResult<T> Success(T data, string correlationId) =>
        new(true, data, null, correlationId);

    public static ToolResult<T> Failure(ToolError error, string correlationId) =>
        new(false, default, error, correlationId);
}
