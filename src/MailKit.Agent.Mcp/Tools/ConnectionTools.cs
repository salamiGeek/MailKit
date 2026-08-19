using System.ComponentModel;
using System.Text.Json.Serialization;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Errors;
using ModelContextProtocol.Server;

namespace MailKit.Agent.Mcp.Tools;

public sealed record ConnectionTestRequest(
    [property: JsonPropertyName("account_id")] string AccountId,
    [property: JsonPropertyName("protocols")] IReadOnlyList<string>? Protocols = null);

[McpServerToolType]
public sealed class ConnectionTools
{
    [McpServerTool(Name = "account_connection_test", UseStructuredContent = true)]
    [Description(
        "Tests IMAP, POP3, and SMTP connectivity and authentication for one account using its stored credential. "
        + "Never accepts passwords or tokens.")]
    public static Task<ToolResult<IReadOnlyList<ProtocolConnectionResult>>> TestAsync(
        [Description(
            "Account selection plus an optional protocol subset (imap, pop3, smtp). "
            + "Defaults to every protocol configured for the account.")]
            ConnectionTestRequest request,
        ConnectionApplication application,
        CancellationToken cancellationToken) =>
        application.TestAsync(request.AccountId, request.Protocols, cancellationToken);
}
