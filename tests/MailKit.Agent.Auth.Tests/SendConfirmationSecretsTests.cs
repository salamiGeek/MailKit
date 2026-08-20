using MailKit.Agent.Auth;

namespace MailKit.Agent.Auth.Tests;

[Platform("Win")]
public class SendConfirmationSecretsTests
{
	[Test]
	public void LoadOrCreateTwiceReturnsSameKeyAndSessionId()
	{
		string directory = CreateDataDirectory();

		var first = SendConfirmationSecrets.LoadOrCreate(directory);
		var second = SendConfirmationSecrets.LoadOrCreate(directory);

		Assert.Multiple(() =>
		{
			Assert.That(second.ConfirmationKey, Is.EqualTo(first.ConfirmationKey),
				"A fresh process must verify tokens issued by the previous one.");
			Assert.That(second.SessionId, Is.EqualTo(first.SessionId),
				"A fresh process must accept commits for preparations of the previous one.");
		});
	}

	[Test]
	public void ConfirmationKeyMeetsTheCodecMinimumLength()
	{
		string directory = CreateDataDirectory();

		var secrets = SendConfirmationSecrets.LoadOrCreate(directory);

		Assert.That(secrets.ConfirmationKey.Length, Is.GreaterThanOrEqualTo(32));
	}

	[Test]
	public void KeyFileNeverContainsThePlaintextKey()
	{
		string directory = CreateDataDirectory();

		var secrets = SendConfirmationSecrets.LoadOrCreate(directory);
		byte[] keyFile = File.ReadAllBytes(Path.Combine(directory, "send-confirmation.key"));

		Assert.That(
			IndexOfWork(keyFile, secrets.ConfirmationKey),
			Is.LessThan(0),
			"The persisted key file must be protected (DPAPI), never plaintext.");
		Assert.That(
			IndexOfWork(keyFile, Convert.FromHexString(secrets.SessionId)),
			Is.LessThan(0),
			"The session identity must not be readable in the key file either.");
	}

	[Test]
	public void CorruptKeyFileIsReplacedWithFreshSecrets()
	{
		string directory = CreateDataDirectory();
		string keyPath = Path.Combine(directory, "send-confirmation.key");
		File.WriteAllBytes(keyPath, "not a protected blob"u8.ToArray());

		var secrets = SendConfirmationSecrets.LoadOrCreate(directory);
		var reloaded = SendConfirmationSecrets.LoadOrCreate(directory);

		Assert.Multiple(() =>
		{
			Assert.That(secrets.ConfirmationKey.Length, Is.GreaterThanOrEqualTo(32));
			Assert.That(reloaded.ConfirmationKey, Is.EqualTo(secrets.ConfirmationKey),
				"The replacement secrets must themselves be stable across reloads.");
		});
	}

	private static string CreateDataDirectory()
	{
		string directory = Path.Combine(
			Path.GetTempPath(), "mailkit-secrets-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static int IndexOfWork(byte[] haystack, byte[] needle)
	{
		for (var i = 0; i <= haystack.Length - needle.Length; i++)
		{
			var matches = true;
			for (var j = 0; j < needle.Length; j++)
			{
				if (haystack[i + j] != needle[j])
				{
					matches = false;
					break;
				}
			}

			if (matches)
				return i;
		}

		return -1;
	}
}
