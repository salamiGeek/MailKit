using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// One durable idempotency record for an outgoing send. Stores only the account ID,
/// idempotency-key hash, Message-Id, state, timestamps, and correlation ID — never
/// the raw idempotency key or any message content.
/// </summary>
public sealed record SendLedgerEntry(
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("state")] SendState State,
    [property: JsonPropertyName("prepared_at")] DateTimeOffset PreparedAt,
    [property: JsonPropertyName("attempted_at")] DateTimeOffset? AttemptedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("correlation_id")] string? CorrelationId);
