using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// The durable, caller-facing status of an idempotent send. Mirrors the ledger entry
/// fields only: account, idempotency-key hash, Message-Id, state, and timestamps.
/// </summary>
public sealed record SendStatus(
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash,
    [property: JsonPropertyName("message_id")] string MessageId,
    [property: JsonPropertyName("state")] SendState State,
    [property: JsonPropertyName("prepared_at")] DateTimeOffset PreparedAt,
    [property: JsonPropertyName("attempted_at")] DateTimeOffset? AttemptedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt);
