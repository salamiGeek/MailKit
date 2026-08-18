using System.Text.Json.Serialization;
using MailKit.Agent.Core.Serialization;

namespace MailKit.Agent.Core.Policy;

[JsonConverter(typeof(LowerSnakeCaseEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    ReadOnly,
    RecoverableWrite,
    ExternalOrIrreversible
}
