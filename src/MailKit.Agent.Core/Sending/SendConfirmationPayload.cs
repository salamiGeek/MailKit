using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// The HMAC-protected payload of a send confirmation token. It binds the preparation
/// identity, account, canonical content hash, idempotency-key hash, caller session,
/// and expiry — never recipients, subject, body, attachment names, or MIME bytes.
/// </summary>
public sealed record SendConfirmationPayload(
    [property: JsonPropertyName("preparation_id")] string PreparationId,
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("content_hash")] string ContentHash,
    [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
