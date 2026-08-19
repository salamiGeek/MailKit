using System.Security.Cryptography;

namespace MailKit.Agent.Mcp;

/// <summary>
/// A random 256-bit per-process session identity used when the MCP transport does
/// not expose a session ID (stdio). It binds each <c>send_prepare</c>/<c>send_commit</c>
/// pair to one agent process. The value is never logged, persisted, or returned to
/// clients.
/// </summary>
public sealed class StdioSessionIdentity
{
    public StdioSessionIdentity()
    {
        byte[] identifier = RandomNumberGenerator.GetBytes(32);
        try
        {
            Id = Convert.ToHexString(identifier).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(identifier);
        }
    }

    public string Id { get; }
}
