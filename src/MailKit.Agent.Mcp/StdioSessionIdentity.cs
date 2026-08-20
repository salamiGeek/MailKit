using System.Security.Cryptography;

namespace MailKit.Agent.Mcp;

/// <summary>
/// The session identity that binds each <c>send_prepare</c>/<c>send_commit</c> pair.
/// The default constructor derives a random 256-bit per-process identity; the host
/// instead composes the class with the persisted installation identity from
/// <c>SendConfirmationSecrets</c> so a commit in a restarted server process still
/// satisfies the session check for a preparation made by its predecessor (bounded
/// by the confirmation token's TTL). The value is never logged and never persisted
/// in the clear; it reaches clients only inside the HMAC-integrity-protected,
/// one-time confirmation token payload, where it is opaque without the data
/// directory's protected key and confers no usable capability. The payload itself
/// stays secret-free (it never carries recipients, subject, body, attachment
/// names, or MIME bytes).
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

    public StdioSessionIdentity(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }
}
