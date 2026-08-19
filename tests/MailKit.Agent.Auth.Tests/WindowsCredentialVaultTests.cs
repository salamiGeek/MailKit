using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Auth.Tests;

[Platform("Win")]
public class WindowsCredentialVaultTests
{
	[Test]
	public async Task RoundTripsAndDeletesOnlyTheNamedCredential()
	{
		var accountId = "test_" + Guid.NewGuid().ToString("N");
		var vault = new WindowsCredentialVault();
		try
		{
			await vault.SetPasswordAsync(
				accountId,
				"user@example.test",
				"secret-value".AsMemory(),
				CancellationToken.None);
			Assert.That(
				(await vault.GetStatusAsync(accountId, CancellationToken.None)).Configured,
				Is.True);
			using var lease = await vault.GetPasswordAsync(accountId, CancellationToken.None);
			Assert.That(
				lease.CreateNetworkCredential("user@example.test").Password,
				Is.EqualTo("secret-value"));
		}
		finally
		{
			await vault.DeletePasswordAsync(accountId, CancellationToken.None);
		}

		Assert.That(
			(await vault.GetStatusAsync(accountId, CancellationToken.None)).Configured,
			Is.False);
	}

	[Test]
	public void MissingCredentialThrowsTypedNotConfiguredError()
	{
		var accountId = "test_" + Guid.NewGuid().ToString("N");
		var vault = new WindowsCredentialVault();

		var exception = Assert.ThrowsAsync<CredentialVaultException>(async () =>
			await vault.GetPasswordAsync(accountId, CancellationToken.None));

		Assert.That(exception!.Code, Is.EqualTo("credential.not_configured"));
	}

	[Test]
	public void InvalidAccountIdIsRejectedBeforeNativeAccess()
	{
		var vault = new WindowsCredentialVault();

		Assert.ThrowsAsync<ArgumentException>(async () =>
			await vault.GetStatusAsync("../unsafe", CancellationToken.None));
	}

	[Test]
	public async Task OversizedPasswordBlobIsRejectedBeforeNativeWrite()
	{
		var accountId = "test_" + Guid.NewGuid().ToString("N");
		var vault = new WindowsCredentialVault();
		try
		{
			Assert.ThrowsAsync<ArgumentException>(async () =>
				await vault.SetPasswordAsync(
					accountId,
					"user@example.test",
					new char[1281],
					CancellationToken.None));
		}
		finally
		{
			await vault.DeletePasswordAsync(accountId, CancellationToken.None);
		}
	}
}
