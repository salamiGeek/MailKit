using System.Security.Cryptography;
using System.Text;
using MailKit.Agent.Auth.Native;

namespace MailKit.Agent.Auth;

/// <summary>
/// The send-confirmation secrets shared by every server process for one data
/// directory: the 256-bit HMAC key that signs and verifies one-time confirmation
/// tokens, and the installation session identity embedded in their payloads. The
/// pair is generated once, stored under <c>&lt;data&gt;/send-confirmation.key</c>
/// protected with Windows DPAPI (current user), and reloaded by later processes, so
/// a <c>send_prepare</c> issued before a restart remains committable by its
/// successor within the token TTL. On non-Windows platforms DPAPI is unavailable
/// and the secrets stay per-process random (restarts invalidate pending tokens,
/// the historical behavior). An unreadable or corrupt key file is replaced with
/// fresh secrets, which safely invalidates any previously issued token.
/// </summary>
public sealed record SendConfirmationSecrets(byte[] ConfirmationKey, string SessionId)
{
	internal const string KeyFileName = "send-confirmation.key";
	private const int SecretLength = 64;

	// Fixed secondary entropy: DPAPI already binds the blob to the current user;
	// this only distinguishes the blob's purpose from other protected data.
	private static readonly byte[] ProtectionEntropy =
		"mailkit-agent/send-confirmation"u8.ToArray();

	public static SendConfirmationSecrets LoadOrCreate(string dataDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

		if (!OperatingSystem.IsWindows())
			return CreateRandom();

		string path = Path.Combine(dataDirectory, KeyFileName);
		try
		{
			if (File.Exists(path))
			{
				byte[] secrets = DpapiNative.Unprotect(
					File.ReadAllBytes(path), ProtectionEntropy);
				if (secrets.Length == SecretLength)
					return FromSecretBytes(secrets);
			}
		}
		catch (Exception exception) when (
			exception is CryptographicException or IOException or UnauthorizedAccessException)
		{
			// Undecryptable (wrong user, tampered) or unreadable: fall through and
			// replace the file with fresh secrets.
		}

		SendConfirmationSecrets fresh = CreateRandom();
		Write(path, DpapiNative.Protect(ToSecretBytes(fresh), ProtectionEntropy));
		return fresh;
	}

	private static SendConfirmationSecrets FromSecretBytes(byte[] secrets) => new(
		secrets[..32],
		Convert.ToHexString(secrets[32..]).ToLowerInvariant());

	private static byte[] ToSecretBytes(SendConfirmationSecrets secrets)
	{
		byte[] session = Convert.FromHexString(secrets.SessionId);
		var combined = new byte[SecretLength];
		secrets.ConfirmationKey.CopyTo(combined, 0);
		session.CopyTo(combined, 32);
		return combined;
	}

	private static SendConfirmationSecrets CreateRandom() => FromSecretBytes(
		RandomNumberGenerator.GetBytes(SecretLength));

	private static void Write(string path, byte[] protectedBlob)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
		try
		{
			File.WriteAllBytes(temporary, protectedBlob);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}
}
