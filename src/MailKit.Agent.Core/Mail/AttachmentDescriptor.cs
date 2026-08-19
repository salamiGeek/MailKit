using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record AttachmentDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("size")] long? Size,
    [property: JsonPropertyName("is_inline")] bool IsInline,
    [property: JsonPropertyName("content_id")] string? ContentId);
