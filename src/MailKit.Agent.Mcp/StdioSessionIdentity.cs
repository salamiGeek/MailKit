using System.Security.Cryptography;

namespace MailKit.Agent.Mcp;

/// <summary>
/// A random 256-bit per-process session identity used when the MCP transport does
/// not expose a session ID (stdio). It binds each <c>send_prepare</c>/<c>send_commit</c>
/// pair to one agent process. The value is never logged and never persisted; it
/// reaches clients only inside the HMAC-integrity-protected, one-time confirmation
/// token payload, where it is opaque without the per-process key and confers no
/// usable capability. The payload itself stays secret-free (it never carries
/// recipients, subject, body, attachment names, or MIME bytes).
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
