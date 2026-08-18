namespace MailKit.Agent.Core.Paging;

public sealed record CursorPayload(
    string AccountId,
    string Scope,
    string Position,
    DateTimeOffset ExpiresAt);
