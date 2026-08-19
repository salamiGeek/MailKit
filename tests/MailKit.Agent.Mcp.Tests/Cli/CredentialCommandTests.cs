using MailKit.Agent.Auth;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Mcp.Cli;

namespace MailKit.Agent.Mcp.Tests.Cli;

public class CredentialCommandTests
{
	[Test]
	public async Task SetReadsSecretLocallyAndUsesTheProfilesUsername()
	{
		var fakeVault = new FakeCredentialVault();
		var fakeConsole = new FakeSecretConsole("secret-value");
		var command = new CredentialCommand(
			new FakeProfileStore(CreateProfile("personal")),
			fakeVault,
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "set", "--account", "personal"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.EqualTo(0));
			Assert.That(fakeVault.LastAccountId, Is.EqualTo("personal"));
			Assert.That(fakeVault.LastUsername, Is.EqualTo("user@example.test"));
			Assert.That(fakeConsole.Output, Does.Not.Contain("secret-value"));
			Assert.That(fakeConsole.Output, Does.Contain("Credential configured."));
			Assert.Throws<ObjectDisposedException>(() =>
				fakeConsole.LastBuffer!.Characters.ToArray());
		});
	}

	[Test]
	public async Task SetRejectsMissingProfileBeforeReadingSecret()
	{
		var fakeConsole = new FakeSecretConsole("secret-value");
		var command = new CredentialCommand(
			new FakeProfileStore(),
			new FakeCredentialVault(),
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "set", "--account", "missing"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.EqualTo(1));
			Assert.That(fakeConsole.ReadCount, Is.Zero);
			Assert.That(fakeConsole.Output, Does.Contain("Account profile was not found."));
		});
	}

	[Test]
	public async Task UnsupportedPlatformReturnsStableErrorWithoutExceptionDetails()
	{
		var fakeVault = new FakeCredentialVault { PlatformUnsupported = true };
		var fakeConsole = new FakeSecretConsole();
		var command = new CredentialCommand(
			new FakeProfileStore(CreateProfile("personal")),
			fakeVault,
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "status", "--account", "personal"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.EqualTo(1));
			Assert.That(fakeConsole.Output, Does.Contain("credential.platform_unsupported"));
			Assert.That(fakeConsole.Output, Does.Not.Contain(nameof(CredentialVaultException)));
		});
	}

	[TestCase(true, "Credential is configured.")]
	[TestCase(false, "Credential is not configured.")]
	public async Task StatusReportsOnlyWhetherCredentialExists(bool configured, string expected)
	{
		var fakeVault = new FakeCredentialVault { Configured = configured };
		var fakeConsole = new FakeSecretConsole();
		var command = new CredentialCommand(
			new FakeProfileStore(CreateProfile("personal")),
			fakeVault,
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "status", "--account", "personal"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.Zero);
			Assert.That(fakeConsole.Output, Does.Contain(expected));
		});
	}

	[Test]
	public async Task DeleteTargetsOnlyTheNamedAccount()
	{
		var fakeVault = new FakeCredentialVault { Configured = true };
		var fakeConsole = new FakeSecretConsole();
		var command = new CredentialCommand(
			new FakeProfileStore(CreateProfile("personal")),
			fakeVault,
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "delete", "--account", "personal"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.Zero);
			Assert.That(fakeVault.LastAccountId, Is.EqualTo("personal"));
			Assert.That(fakeConsole.Output, Does.Contain("Credential deleted."));
		});
	}

	[TestCase("--unknown")]
	[TestCase("--password")]
	public async Task SecretOrUnknownOptionsAreRejectedBeforeReadingInput(string option)
	{
		var fakeVault = new FakeCredentialVault();
		var fakeConsole = new FakeSecretConsole("secret-value");
		var command = new CredentialCommand(
			new FakeProfileStore(CreateProfile("personal")),
			fakeVault,
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "set", "--account", "personal", option, "secret-value"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.EqualTo(2));
			Assert.That(fakeConsole.ReadCount, Is.Zero);
			Assert.That(fakeVault.LastAccountId, Is.Null);
			Assert.That(fakeConsole.Output, Does.Not.Contain(option));
			Assert.That(fakeConsole.Output, Does.Not.Contain("secret-value"));
		});
	}

	[Test]
	public async Task CancelledSecretInputReturnsConventionalExitCode()
	{
		var fakeConsole = new FakeSecretConsole { CancelRead = true };
		var command = new CredentialCommand(
			new FakeProfileStore(CreateProfile("personal")),
			new FakeCredentialVault(),
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "set", "--account", "personal"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.EqualTo(130));
			Assert.That(fakeConsole.Output, Does.Contain("Operation canceled."));
		});
	}

	[Test]
	public async Task HelpListsOnlyInteractiveCredentialCommandsAndAccountOption()
	{
		var fakeConsole = new FakeSecretConsole();
		var command = new CredentialCommand(
			new FakeProfileStore(),
			new FakeCredentialVault(),
			fakeConsole);

		var exitCode = await command.RunAsync(
			["account", "credential", "--help"],
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(exitCode, Is.Zero);
			Assert.That(fakeConsole.Output, Does.Contain("set"));
			Assert.That(fakeConsole.Output, Does.Contain("status"));
			Assert.That(fakeConsole.Output, Does.Contain("delete"));
			Assert.That(fakeConsole.Output, Does.Contain("--account"));
			Assert.That(fakeConsole.Output, Does.Not.Contain("--password").IgnoreCase);
			Assert.That(fakeConsole.Output, Does.Not.Contain("--token").IgnoreCase);
			Assert.That(fakeConsole.Output, Does.Not.Contain("--secret").IgnoreCase);
		});
	}

	private static AccountProfile CreateProfile(string id) =>
		new(
			id,
			"Personal",
			"user@example.test",
			AuthenticationKind.Password,
			new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
			null,
			null);

	private sealed class FakeProfileStore(params AccountProfile[] profiles) : IAccountProfileStore
	{
		public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<AccountProfile>>(profiles);

		public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken) =>
			Task.FromResult(profiles.SingleOrDefault(profile => profile.Id == id));

		public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
			throw new NotSupportedException();
	}

	private sealed class FakeCredentialVault : IAccountCredentialVault
	{
		public bool Configured { get; set; }
		public bool PlatformUnsupported { get; set; }
		public string? LastAccountId { get; private set; }
		public string? LastUsername { get; private set; }

		public ValueTask<CredentialStatus> GetStatusAsync(
			string accountId,
			CancellationToken cancellationToken)
		{
			ThrowIfUnsupported();
			LastAccountId = accountId;
			return ValueTask.FromResult(new CredentialStatus(
				Configured,
				Configured ? CredentialKind.Password : null));
		}

		public ValueTask<PasswordCredentialLease> GetPasswordAsync(
			string accountId,
			CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public ValueTask SetPasswordAsync(
			string accountId,
			string username,
			ReadOnlyMemory<char> password,
			CancellationToken cancellationToken)
		{
			ThrowIfUnsupported();
			LastAccountId = accountId;
			LastUsername = username;
			Configured = true;
			return ValueTask.CompletedTask;
		}

		public ValueTask<bool> DeletePasswordAsync(
			string accountId,
			CancellationToken cancellationToken)
		{
			ThrowIfUnsupported();
			LastAccountId = accountId;
			var deleted = Configured;
			Configured = false;
			return ValueTask.FromResult(deleted);
		}

		private void ThrowIfUnsupported()
		{
			if (PlatformUnsupported)
				throw CredentialVaultException.PlatformUnsupported();
		}
	}

	private sealed class FakeSecretConsole : ISecretConsole
	{
		private readonly char[] _secret;
		private readonly List<string> _output = [];

		public FakeSecretConsole(string secret = "")
		{
			_secret = secret.ToCharArray();
		}

		public bool CancelRead { get; init; }
		public int ReadCount { get; private set; }
		public SecretBuffer? LastBuffer { get; private set; }
		public string Output => string.Join(Environment.NewLine, _output);

		public ValueTask<SecretBuffer> ReadSecretAsync(
			string prompt,
			CancellationToken cancellationToken)
		{
			ReadCount++;
			_output.Add(prompt);
			if (CancelRead)
				throw new OperationCanceledException(cancellationToken);

			LastBuffer = new SecretBuffer(_secret.AsSpan());
			return ValueTask.FromResult(LastBuffer);
		}

		public void WriteLine(string value) => _output.Add(value);
	}
}
