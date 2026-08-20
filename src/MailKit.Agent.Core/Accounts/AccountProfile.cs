using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Accounts;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<TlsMode>))]
public enum TlsMode
{
    Plain,
    StartTls,
    ImplicitTls
}

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<AuthenticationKind>))]
public enum AuthenticationKind
{
    Password,
    OAuth2
}

/// <summary>
/// How a confirmed send commit executes for one account: deliver over SMTP after
/// the local human-approval dialog (<see cref="ConfirmDialog"/>), or append the
/// composed message to the account's IMAP Drafts folder for human review and
/// manual sending (<see cref="Drafts"/> — the agent can never deliver in that mode).
/// </summary>
[JsonConverter(typeof(LowerSnakeCaseEnumConverter<SendMode>))]
public enum SendMode
{
    ConfirmDialog,
    Drafts
}

public sealed record EndpointSettings(
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("tls")] TlsMode Tls);

public sealed record AccountProfile(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("authentication")] AuthenticationKind Authentication,
    [property: JsonPropertyName("imap")] EndpointSettings? Imap,
    [property: JsonPropertyName("pop3")] EndpointSettings? Pop3,
    [property: JsonPropertyName("smtp")] EndpointSettings? Smtp,
    [property: JsonPropertyName("send_mode")] SendMode SendMode = SendMode.ConfirmDialog);
