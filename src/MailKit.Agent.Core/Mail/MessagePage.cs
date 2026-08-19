using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record MessagePage(
    [property: JsonPropertyName("messages")] IReadOnlyList<MessageEnvelope> Messages,
    [property: JsonPropertyName("next_offset")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NextOffset)
{
    [JsonPropertyName("next_cursor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }
}
