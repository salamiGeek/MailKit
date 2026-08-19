using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// A single typed mailbox used in an outgoing message draft. Only the address is
/// structurally significant; the optional display name is used for preview text only.
/// </summary>
public sealed record OutgoingMailbox(
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("address")] string Address)
{
    [JsonIgnore]
    public string DisplayText =>
        string.IsNullOrWhiteSpace(DisplayName) ? Address : $"{DisplayName} <{Address}>";
}

/// <summary>
/// The protocol-agnostic description of a message the caller wants to send.
/// Contains typed mailbox lists for To/Cc/Bcc, an optional From mailbox,
/// subject and body alternatives, and local attachment file paths.
/// </summary>
public sealed record OutgoingMessageDraft(
    [property: JsonPropertyName("to")] IReadOnlyList<OutgoingMailbox>? To,
    [property: JsonPropertyName("cc")] IReadOnlyList<OutgoingMailbox>? Cc,
    [property: JsonPropertyName("bcc")] IReadOnlyList<OutgoingMailbox>? Bcc,
    [property: JsonPropertyName("from")] OutgoingMailbox? From,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("text_body")] string? TextBody,
    [property: JsonPropertyName("html_body")] string? HtmlBody,
    [property: JsonPropertyName("attachment_paths")] IReadOnlyList<string>? AttachmentPaths);
