using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record MessageEnvelope(
    [property: JsonPropertyName("reference")] MessageReference Reference,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("from")] IReadOnlyList<string> From,
    [property: JsonPropertyName("to")] IReadOnlyList<string> To,
    [property: JsonPropertyName("date")] DateTimeOffset? Date,
    [property: JsonPropertyName("internal_date")] DateTimeOffset? InternalDate,
    [property: JsonPropertyName("size")] uint? Size,
    [property: JsonPropertyName("flags")] IReadOnlyList<string> Flags,
    [property: JsonPropertyName("has_attachments")] bool HasAttachments);
