namespace MailKit.Agent.Auth.Tests;

public class UnsupportedCredentialVaultTests
{
	[Test]
	public void EveryOperationReturnsTheStablePlatformUnsupportedError()
	{
		var vault = new UnsupportedCredentialVault();

		var exceptions = new[]
		{
			Assert.ThrowsAsync<CredentialVaultException>(async () =>
				await vault.GetStatusAsync("personal", CancellationToken.None)),
			Assert.ThrowsAsync<CredentialVaultException>(async () =>
				await vault.GetPasswordAsync("personal", CancellationToken.None)),
			Assert.ThrowsAsync<CredentialVaultException>(async () =>
				await vault.SetPasswordAsync(
					"personal",
					"user@example.test",
					ReadOnlyMemory<char>.Empty,
					CancellationToken.None)),
			Assert.ThrowsAsync<CredentialVaultException>(async () =>
				await vault.DeletePasswordAsync("personal", CancellationToken.None))
		};

		Assert.That(
			exceptions.Select(exception => exception!.Code),
			Is.All.EqualTo("credential.platform_unsupported"));
	}
}
