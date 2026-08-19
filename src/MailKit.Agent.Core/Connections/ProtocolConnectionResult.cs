using System.Text.Json.Serialization;
using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Connections;

public sealed record ProtocolConnectionResult(
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("tls_established")] bool TlsEstablished,
    [property: JsonPropertyName("authenticated")] bool Authenticated,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("error")] ToolError? Error);
