using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Contracts;

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("network_listener_enabled")] bool NetworkListenerEnabled)
{
    public static ServerInfo Foundation { get; } =
        new("mailkit-agent", "0.1.0", "stdio", false);
}
