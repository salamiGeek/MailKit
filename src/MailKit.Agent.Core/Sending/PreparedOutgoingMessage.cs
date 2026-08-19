using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// A prepared outgoing message held in memory between the prepare and commit phases.
/// The MIME bytes are the serialized DATA payload and must never contain Bcc headers.
/// Blind-copy recipients are retained only in envelope metadata
/// (<see cref="EnvelopeRecipients"/>) so the SMTP envelope can deliver them while the
/// transmitted message body stays free of Bcc information.
/// </summary>
public sealed record PreparedOutgoingMessage(
    [property: JsonPropertyName("preparation_id")] string PreparationId,
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("mime_message")] byte[] MimeMessage,
    [property: JsonPropertyName("envelope_sender")] string? EnvelopeSender,
    [property: JsonPropertyName("envelope_recipients")] IReadOnlyList<string> EnvelopeRecipients,
    [property: JsonPropertyName("preview")] SendPreview Preview,
    [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
