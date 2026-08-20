using System.Reflection;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Connections;
using MailKit.Agent.Mail.Imap;
using MailKit.Agent.Mail.Pop3;
using MailKit.Agent.Mail.Sending;
using MailKit.Agent.Mail.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace MailKit.Agent.Mcp.Tests;

/// <summary>
/// Guards the DI wiring that makes the registered <see cref="ConnectionGate"/>
/// singleton the gate every protocol gateway holds leases through, so the
/// per-account/per-protocol/global connection limits are enforced process-wide.
/// </summary>
public sealed class MailRuntimeRegistrationTests
{
	[Test]
	public void RuntimeGatewaysShareTheRegisteredConnectionGate()
	{
		string dataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		try
		{
			var services = new ServiceCollection();
			McpServerHost.ConfigureMailRuntime(services, dataDirectory);
			using ServiceProvider provider = services.BuildServiceProvider();

			ConnectionGate gate = provider.GetRequiredService<ConnectionGate>();
			var imap = (ImapGateway)provider.GetRequiredService<IImapGateway>();
			var pop3 = (Pop3Gateway)provider.GetRequiredService<IPop3Gateway>();
			var smtp = (SmtpGateway)provider.GetRequiredService<ISmtpGateway>();
			var draftStore = (ImapDraftStore)provider.GetRequiredService<IDraftMessageStore>();

			Assert.Multiple(() =>
			{
				Assert.That(GateOf(imap), Is.SameAs(gate),
					"ImapGateway must acquire through the registered gate singleton.");
				Assert.That(GateOf(pop3), Is.SameAs(gate),
					"Pop3Gateway must acquire through the registered gate singleton.");
				Assert.That(GateOf(smtp), Is.SameAs(gate),
					"SmtpGateway must acquire through the registered gate singleton.");
				Assert.That(GateOf(draftStore), Is.SameAs(gate),
					"ImapDraftStore must acquire through the registered gate singleton.");
			});
		}
		finally
		{
			if (Directory.Exists(dataDirectory))
				Directory.Delete(dataDirectory, recursive: true);
		}
	}

	[Test]
	public void ProductionWiringRegistersAnOsLocalApproverNeverTheAutomaticOne()
	{
		// The local human-approval gate is a hard server-side requirement: the
		// production (non-test-mode) wiring must never hand out the automatic
		// approver, otherwise an agent could chain prepare+commit unattended.
		string dataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		try
		{
			var services = new ServiceCollection();
			// Mirror the host-level registrations RunAsync contributes (profile
			// store, vault, policy, clock) plus ConfigureMailStorage, so the mail
			// runtime under test is assembled exactly like a production host and
			// SendApplication itself can be resolved for inspection.
			services.AddSingleton<IAccountProfileStore>(_ => new JsonAccountProfileStore(dataDirectory));
			services.AddSingleton<IAccountCredentialVault, UnusedCredentialVault>();
			services.AddSingleton(OperationPolicy.Default);
			services.AddSingleton(TimeProvider.System);
			McpServerHost.ConfigureMailStorage(services, dataDirectory);
			McpServerHost.ConfigureMailRuntime(services, dataDirectory);
			using ServiceProvider provider = services.BuildServiceProvider();

			ISendCommitApprover approver = provider.GetRequiredService<ISendCommitApprover>();
			SendApplication application = provider.GetRequiredService<SendApplication>();

			Assert.Multiple(() =>
			{
#if DEBUG
				// In Release the automatic approver type is not compiled at all, so
				// the risk this assertion guards only exists in Debug builds.
				Assert.That(approver, Is.Not.InstanceOf<AutomaticSendCommitApprover>(),
					"Production hosts must never register the automatic approver.");
#endif
				Assert.That(
					approver,
					Is.InstanceOf(OperatingSystem.IsWindows()
						? typeof(WindowsSendCommitApprover)
						: typeof(UnavailableSendCommitApprover)),
					"Non-Windows or desktop-less hosts must report approval as unavailable.");
				Assert.That(ApproverOf(application), Is.SameAs(approver),
					"SendApplication must consume the registered approver singleton.");
			});
		}
		finally
		{
			if (Directory.Exists(dataDirectory))
				Directory.Delete(dataDirectory, recursive: true);
		}
	}

	[Test]
	public void ProductionWiringRegistersTheImapDraftStoreForSendApplication()
	{
		// The drafts send mode must reach the real IMAP-backed store in production:
		// a missing registration would make drafts-mode commits unresolvable, and a
		// fake would silently skip the actual Drafts-folder append.
		string dataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
		try
		{
			var services = new ServiceCollection();
			services.AddSingleton<IAccountProfileStore>(_ => new JsonAccountProfileStore(dataDirectory));
			services.AddSingleton<IAccountCredentialVault, UnusedCredentialVault>();
			services.AddSingleton(OperationPolicy.Default);
			services.AddSingleton(TimeProvider.System);
			McpServerHost.ConfigureMailStorage(services, dataDirectory);
			McpServerHost.ConfigureMailRuntime(services, dataDirectory);
			using ServiceProvider provider = services.BuildServiceProvider();

			IDraftMessageStore draftStore = provider.GetRequiredService<IDraftMessageStore>();
			SendApplication application = provider.GetRequiredService<SendApplication>();

			Assert.Multiple(() =>
			{
				Assert.That(draftStore, Is.InstanceOf<ImapDraftStore>());
				Assert.That(DraftStoreOf(application), Is.SameAs(draftStore),
					"SendApplication must consume the registered draft store singleton.");
			});
		}
		finally
		{
			if (Directory.Exists(dataDirectory))
				Directory.Delete(dataDirectory, recursive: true);
		}
	}

	private sealed class UnusedCredentialVault : IAccountCredentialVault
	{
		public ValueTask<CredentialStatus> GetStatusAsync(
			string accountId, CancellationToken cancellationToken) =>
			throw new NotSupportedException("The registration test never sends mail.");

		public ValueTask<PasswordCredentialLease> GetPasswordAsync(
			string accountId, CancellationToken cancellationToken) =>
			throw new NotSupportedException("The registration test never sends mail.");

		public ValueTask SetPasswordAsync(
			string accountId,
			string username,
			ReadOnlyMemory<char> password,
			CancellationToken cancellationToken) =>
			throw new NotSupportedException("The registration test never sends mail.");

		public ValueTask<bool> DeletePasswordAsync(
			string accountId, CancellationToken cancellationToken) =>
			throw new NotSupportedException("The registration test never sends mail.");
	}

	private static ConnectionGate GateOf(object gateway) =>
		(ConnectionGate)gateway.GetType()
			.GetField("connectionGate", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(gateway)!;

	private static ISendCommitApprover ApproverOf(SendApplication application) =>
		(ISendCommitApprover)application.GetType()
			.GetField("sendApprover", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(application)!;

	private static IDraftMessageStore DraftStoreOf(SendApplication application) =>
		(IDraftMessageStore)application.GetType()
			.GetField("draftStore", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(application)!;
}
