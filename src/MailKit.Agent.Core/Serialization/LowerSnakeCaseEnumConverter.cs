using System.Text.Json;
using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Serialization;

public sealed class LowerSnakeCaseEnumConverter<TEnum>()
    : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.SnakeCaseLower)
    where TEnum : struct, Enum;
