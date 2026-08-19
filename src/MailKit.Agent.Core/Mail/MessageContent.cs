using System.Text.Json.Serialization;
using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Mail;

public sealed record MessageHeader(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record MimePartSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("disposition")] string? Disposition,
    [property: JsonPropertyName("file_name")] string? FileName,
    [property: JsonPropertyName("is_attachment")] bool IsAttachment);

public sealed record MessageContent
{
    [JsonPropertyName("headers")]
    public IReadOnlyList<MessageHeader> Headers { get; init; } = Array.Empty<MessageHeader>();

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("html")]
    public string? Html { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("original_character_count")]
    public int OriginalCharacterCount { get; init; }

    [JsonPropertyName("returned_character_count")]
    public int ReturnedCharacterCount { get; init; }

    [JsonPropertyName("remote_resources_loaded")]
    public bool RemoteResourcesLoaded { get; init; }

    [JsonPropertyName("untrusted")]
    public bool Untrusted { get; init; } = true;

    [JsonPropertyName("mime_summary")]
    public IReadOnlyList<MimePartSummary> MimeSummary { get; init; } = Array.Empty<MimePartSummary>();

    [JsonPropertyName("attachments")]
    public IReadOnlyList<AttachmentDescriptor> Attachments { get; init; } = Array.Empty<AttachmentDescriptor>();

    [JsonPropertyName("read_state_supported")]
    public bool ReadStateSupported { get; init; }

    [JsonPropertyName("is_read")]
    public bool? IsRead { get; init; }

    [JsonPropertyName("read_state_updated")]
    public bool ReadStateUpdated { get; init; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<ToolError> Warnings { get; init; } = Array.Empty<ToolError>();
}
