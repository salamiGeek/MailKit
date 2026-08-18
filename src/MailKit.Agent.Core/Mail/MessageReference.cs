using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Mail;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<MailProtocol>))]
public enum MailProtocol
{
    Imap,
    Pop3
}

public sealed record MessageReference(
    [property: JsonPropertyName("protocol")] MailProtocol Protocol,
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("folder_id")] string? FolderId,
    [property: JsonPropertyName("uid_validity")] uint? UidValidity,
    [property: JsonPropertyName("uid")] uint? Uid,
    [property: JsonPropertyName("uidl")] string? Uidl)
{
    public static MessageReference ForImap(string accountId, string folderId, uint uidValidity, uint uid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);

        if (uidValidity == 0)
            throw new ArgumentOutOfRangeException(nameof(uidValidity));
        if (uid == 0)
            throw new ArgumentOutOfRangeException(nameof(uid));

        return new(MailProtocol.Imap, accountId, folderId, uidValidity, uid, null);
    }

    public static MessageReference ForPop3(string accountId, string uidl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(uidl);

        return new(MailProtocol.Pop3, accountId, null, null, null, uidl);
    }
}
