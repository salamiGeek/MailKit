using System.Text.Json.Serialization;
using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// The redacted, caller-facing preview of a prepared outgoing message. It shows what
/// will be sent (recipients, subject, attachment names, a short body preview), how a
/// confirmed commit will execute (<see cref="SendMode"/>), plus the one-time
/// confirmation token, but never contains full bodies, MIME bytes, or secrets.
/// </summary>
public sealed record SendPreview(
    [property: JsonPropertyName("preparation_id")] string PreparationId,
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("to")] IReadOnlyList<string> To,
    [property: JsonPropertyName("cc")] IReadOnlyList<string> Cc,
    [property: JsonPropertyName("bcc")] IReadOnlyList<string> Bcc,
    [property: JsonPropertyName("send_mode")] SendMode SendMode,
    [property: JsonPropertyName("subject")] string? Subject,
    [property: JsonPropertyName("text_preview")] string? TextPreview,
    [property: JsonPropertyName("attachment_count")] int AttachmentCount,
    [property: JsonPropertyName("attachment_names")] IReadOnlyList<string> AttachmentNames,
    [property: JsonPropertyName("prepared_at")] DateTimeOffset PreparedAt,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash,
    [property: JsonPropertyName("confirmation_token")] string ConfirmationToken);
