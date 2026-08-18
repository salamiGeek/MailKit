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
    [property: JsonPropertyName("smtp")] EndpointSettings? Smtp);
