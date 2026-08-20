using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Paging;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Serialization;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Attachments;
using MailKit.Agent.Mail.Connections;
using MailKit.Agent.Mail.Imap;
using MailKit.Agent.Mail.Mime;
using MailKit.Agent.Mail.Pop3;
using MailKit.Agent.Mail.Sending;
using MailKit.Agent.Mail.Smtp;
using MailKit.Agent.Mcp.Testing;
using MailKit.Agent.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MailKit.Agent.Mcp;

public static class McpServerHost
{
	public static async Task RunAsync(
		string[] arguments,
		string dataDirectory,
		IAccountCredentialVault vault)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
		ArgumentNullException.ThrowIfNull(vault);

#if DEBUG
		IReadOnlySet<string>? requestedTestFixtures = null;
#endif
		if (TestGatewayRegistration.IsTestModeRequested())
		{
#if DEBUG
			// Fixture switches never reach the generic host configuration.
			requestedTestFixtures = TestGatewayRegistration.ParseRequestedFixtures(arguments);
			arguments = arguments
				.Where(argument => !argument.StartsWith(TestGatewayRegistration.FixtureSwitchPrefix, StringComparison.Ordinal))
				.ToArray();
#else
			throw new InvalidOperationException(
				"MAILKIT_AGENT_TEST_MODE is rejected by production builds.");
#endif
		}

		var builder = Host.CreateApplicationBuilder(arguments);
		builder.Logging.AddConsole(options =>
			options.LogToStandardErrorThreshold = LogLevel.Trace);

		var toolSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
			TypeInfoResolver = new DefaultJsonTypeInfoResolver()
		};
		toolSerializerOptions.Converters.Add(new LowerSnakeCaseEnumConverter<AuthenticationKind>());
		toolSerializerOptions.Converters.Add(new LowerSnakeCaseEnumConverter<TlsMode>());
		builder.Services.AddSingleton<IAccountProfileStore>(
			_ => new JsonAccountProfileStore(dataDirectory));
		builder.Services.AddSingleton(vault);
		builder.Services.AddSingleton(OperationPolicy.Default);
		builder.Services.AddSingleton(TimeProvider.System);
		builder.Services.AddSingleton<StdioSessionIdentity>();
		ConfigureMailStorage(builder.Services, dataDirectory);
		ConfigureMailRuntime(builder.Services, dataDirectory);
#if DEBUG
		if (requestedTestFixtures is { Count: > 0 })
			TestGatewayRegistration.ConfigureServices(builder.Services, requestedTestFixtures);
#endif
		builder.Services
			.AddMcpServer()
			.WithStdioServerTransport()
			.WithTools<DiagnosticsTools>(toolSerializerOptions)
			.WithTools<AccountTools>(toolSerializerOptions)
			.WithTools<ConnectionTools>(toolSerializerOptions)
			.WithTools<ImapTools>(toolSerializerOptions)
			.WithTools<Pop3Tools>(toolSerializerOptions)
			.WithTools<AttachmentTools>(toolSerializerOptions)
			.WithTools<SendTools>(toolSerializerOptions);

		await builder.Build().RunAsync();
	}

	internal static void ConfigureMailRuntime(
		IServiceCollection services,
		string dataDirectory)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

		// Independent random 256-bit keys per process start: cursors and uncommitted
		// confirmations intentionally expire across restarts, while the send ledger
		// preserves terminal and indeterminate delivery state. The keys are never
		// logged, persisted, or returned to clients.
		services.AddSingleton<ICursorCodec>(serviceProvider =>
			new HmacCursorCodec(
				RandomNumberGenerator.GetBytes(32),
				serviceProvider.GetRequiredService<TimeProvider>()));
		services.AddSingleton<ISendConfirmationCodec>(serviceProvider =>
			new HmacSendConfirmationCodec(
				RandomNumberGenerator.GetBytes(32),
				serviceProvider.GetRequiredService<TimeProvider>()));
		services.AddSingleton<ISendLedger>(_ => new JsonSendLedger(dataDirectory));
		services.AddSingleton<IPreparedSendStore, MemoryPreparedSendStore>();
		// Hard local human-approval gate for send commits: the MCP caller cannot
		// produce this factor on its own. Debug test-mode registrations may override
		// it with the automatic approver (see TestGatewayRegistration); Release
		// builds reject test mode, so production always keeps the gate.
		services.AddSingleton<ISendCommitApprover>(_ =>
			OperatingSystem.IsWindows()
				? new WindowsSendCommitApprover()
				: new UnavailableSendCommitApprover());
		services.AddSingleton<MailServiceConnector>();
		services.AddSingleton<ConnectionGate>();
		services.AddSingleton<MimePartLocator>();
		services.AddSingleton<MimeContentService>();
		services.AddSingleton<IImapClientFactory, ImapClientFactory>();
		services.AddSingleton<IPop3ClientFactory, Pop3ClientFactory>();
		services.AddSingleton<ISmtpClientFactory, SmtpClientFactory>();
		services.AddSingleton<IProtocolConnectionTester, ProtocolConnectionTester>();
		services.AddSingleton<IImapGateway, ImapGateway>();
		services.AddSingleton<IPop3Gateway, Pop3Gateway>();
		services.AddSingleton<ISmtpGateway, SmtpGateway>();
		services.AddSingleton<IOutgoingMessageComposer>(serviceProvider =>
			new OutgoingMessageComposer(
				serviceProvider.GetRequiredService<MailFileOptions>(),
				timeProvider: serviceProvider.GetRequiredService<TimeProvider>()));
		services.AddSingleton<ConnectionApplication>();
		services.AddSingleton<MailboxApplication>();
		services.AddSingleton<AttachmentApplication>();
		services.AddSingleton<SendApplication>();
	}

	internal static void ConfigureMailStorage(
		IServiceCollection services,
		string dataDirectory)
	{
		ArgumentNullException.ThrowIfNull(services);
		ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

		MailFileOptions options = MailFileOptionsResolver.Resolve(dataDirectory);
		services.AddSingleton(options);
		services.AddSingleton(MailSafetyLimits.Default);
		services.AddSingleton(new UploadAttachmentPathPolicy(options.UploadRoots));
		services.AddSingleton<IAttachmentWriter, AttachmentService>();
	}
}
