using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Errors;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<ErrorCategory>))]
public enum ErrorCategory
{
    Validation,
    Authentication,
    Authorization,
    Capability,
    Conflict,
    Transient,
    Policy,
    Internal
}
