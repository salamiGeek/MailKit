using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Mail;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<BodyMode>))]
public enum BodyMode
{
    SafeText,
    Html
}
