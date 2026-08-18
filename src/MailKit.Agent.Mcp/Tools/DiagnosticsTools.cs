using System.ComponentModel;
using MailKit.Agent.Core.Contracts;
using MailKit.Agent.Core.Errors;
using ModelContextProtocol.Server;

namespace MailKit.Agent.Mcp.Tools;

[McpServerToolType]
public sealed class DiagnosticsTools
{
    [McpServerTool(Name = "diagnostics_health", UseStructuredContent = true)]
    [Description("Reports local MailKit Agent server identity and transport health without accessing email.")]
    public static ToolResult<ServerInfo> Health()
    {
        var correlationId = Guid.NewGuid().ToString("N");
        return ToolResult<ServerInfo>.Success(ServerInfo.Foundation, correlationId);
    }
}
