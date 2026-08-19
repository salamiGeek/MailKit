using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record MessagePage(
    [property: JsonPropertyName("messages")] IReadOnlyList<MessageEnvelope> Messages,
    [property: JsonPropertyName("next_offset")] int? NextOffset);
