using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record AttachmentSaveResult(
    [property: JsonPropertyName("attachment_id")] string AttachmentId,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes_written")] long BytesWritten);
