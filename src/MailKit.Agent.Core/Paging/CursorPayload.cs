using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Paging;

public sealed record CursorPayload(
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("position")] string Position,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
