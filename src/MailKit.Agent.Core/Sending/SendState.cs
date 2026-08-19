using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Sending;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<SendState>))]
public enum SendState
{
    Prepared,
    Attempting,
    Succeeded,
    Failed,
    Indeterminate
}
