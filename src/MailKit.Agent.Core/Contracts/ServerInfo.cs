using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Contracts;

public sealed record ServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("transport")] string Transport,
    [property: JsonPropertyName("network_listener_enabled")] bool NetworkListenerEnabled)
{
    // Keep in sync with plugins/mailkit-agent/.codex-plugin/plugin.json on every
    // release (see docs/MailKit.Agent/升级指南.md): diagnostics_health reports
    // this value, and deployers rely on it to confirm an upgrade took effect.
    public static ServerInfo Foundation { get; } =
        new("mailkit-agent", "0.2.1", "stdio", false);
}
