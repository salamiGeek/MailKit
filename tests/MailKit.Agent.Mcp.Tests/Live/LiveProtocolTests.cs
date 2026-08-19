using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MailKit.Agent.Mcp.Tests.Live;

/// <summary>
/// Opt-in smoke sequence against a user-configured real mail server. The fixture is
/// marked <see cref="ExplicitAttribute"/> so it never runs in normal test runs or CI,
/// and it performs no <c>MAILKIT_AGENT_TEST_MODE</c> fake-gateway activation. All
/// server and profile settings are read from non-secret environment variables; the
/// account password is obtained exclusively by the spawned server process through
/// <c>WindowsCredentialVault</c> (Credential Manager target
/// <c>MailKit.Agent/account/&lt;account-id&gt;/password</c>) and is never accepted by
/// this test through arguments, environment, or assertion code.
/// </summary>
[TestFixture]
[Explicit("Requires a user-configured real mail server and Windows credential.")]
public sealed class LiveProtocolTests
{
	public const string AccountIdVariable = "MAILKIT_AGENT_LIVE_ACCOUNT_ID";
	public const string UsernameVariable = "MAILKIT_AGENT_LIVE_USERNAME";
	public const string DataDirectoryVariable = "MAILKIT_AGENT_LIVE_DATA_DIR";
	public const string ImapHostVariable = "MAILKIT_AGENT_LIVE_IMAP_HOST";
	public const string ImapPortVariable = "MAILKIT_AGENT_LIVE_IMAP_PORT";
	public const string ImapTlsVariable = "MAILKIT_AGENT_LIVE_IMAP_TLS";
	public const string Pop3HostVariable = "MAILKIT_AGENT_LIVE_POP3_HOST";
	public const string Pop3PortVariable = "MAILKIT_AGENT_LIVE_POP3_PORT";
	public const string Pop3TlsVariable = "MAILKIT_AGENT_LIVE_POP3_TLS";
	public const string SmtpHostVariable = "MAILKIT_AGENT_LIVE_SMTP_HOST";
	public const string SmtpPortVariable = "MAILKIT_AGENT_LIVE_SMTP_PORT";
	public const string SmtpTlsVariable = "MAILKIT_AGENT_LIVE_SMTP_TLS";
	public const string ImapUidVariable = "MAILKIT_AGENT_LIVE_IMAP_UID";
	public const string Pop3UidlVariable = "MAILKIT_AGENT_LIVE_POP3_UIDL";
	public const string AttachmentIdVariable = "MAILKIT_AGENT_LIVE_ATTACHMENT_ID";
	public const string RecipientVariable = "MAILKIT_AGENT_LIVE_RECIPIENT";
	public const string ConfirmMarkReadVariable = "MAILKIT_AGENT_LIVE_CONFIRM_MARK_READ";
	public const string ConfirmSendVariable = "MAILKIT_AGENT_LIVE_CONFIRM_SEND";

	/// <summary>SMTP defaults to port 465 with implicit TLS.</summary>
	public const string DefaultTls = "implicit_tls";

	// The wrapper scripts/Test-MailKitAgentLive.ps1 prints exactly these values in its
	// pre-send preview; LiveSmokeGuardTests asserts the two stay in sync.
	public const string SendSubject = "MailKit Agent live send verification";
	public const string SendTextBody =
		"This MailKit Agent live verification message was sent by scripts/Test-MailKitAgentLive.ps1.";

	private const int PageSize = 50;
	private const int MaxPages = 10;
	private const int PreviewLength = 200;
	private static readonly TimeSpan LiveCallTimeout = TimeSpan.FromSeconds(180);

	[Test]
	public async Task LiveServerProtocolSmokeSequence()
	{
		var settings = LiveSettings.FromEnvironment();
		TestContext.Out.WriteLine(
			$"Live smoke test for account '{settings.AccountId}' "
			+ $"(imap={settings.ImapHost}:{settings.ImapPort}, pop3={settings.Pop3Host}:{settings.Pop3Port}, "
			+ $"smtp={settings.SmtpHost}:{settings.SmtpPort}).");

		bool ownsDataDirectory = settings.DataDirectory is null;
		string dataDirectory = settings.DataDirectory
			?? Path.Combine(Path.GetTempPath(), "mailkit-agent-live-" + Guid.NewGuid().ToString("N"));
		if (ownsDataDirectory)
			Directory.CreateDirectory(dataDirectory);

		await using var server = await LiveStdioServer.StartAsync(dataDirectory, ownsDataDirectory);
		using var cancellation = new CancellationTokenSource(LiveCallTimeout);
		McpClient client = server.Client;

		// The password is never supplied here: the server-side account_profile_put +
		// account_credential_status pair configures and checks the profile against the
		// Windows Credential Manager through WindowsCredentialVault only.
		var profile = DataOf(await client.CallToolAsync(
			"account_profile_put",
			new Dictionary<string, object?>
			{
				["profile"] = new Dictionary<string, object?>
				{
					["id"] = settings.AccountId,
					["display_name"] = "Live smoke " + settings.AccountId,
					["username"] = settings.Username,
					["authentication"] = "password",
					["imap"] = Endpoint(settings.ImapHost, settings.ImapPort, settings.ImapTls),
					["pop3"] = Endpoint(settings.Pop3Host, settings.Pop3Port, settings.Pop3Tls),
					["smtp"] = Endpoint(settings.SmtpHost, settings.SmtpPort, settings.SmtpTls)
				}
			},
			cancellationToken: cancellation.Token), "account_profile_put");

		var credential = DataOf(await client.CallToolAsync(
			"account_credential_status",
			Request(("account_id", settings.AccountId)),
			cancellationToken: cancellation.Token), "account_credential_status");
		Assert.That(
			credential.GetProperty("configured").GetBoolean(),
			Is.True,
			$"No Windows credential is configured for target "
			+ $"'MailKit.Agent/account/{settings.AccountId}/password'. Run "
			+ $"'mailkit-agent account credential set --account {settings.AccountId}' first.");

		// 1. account_connection_test for imap,pop3,smtp.
		var connections = DataOf(await client.CallToolAsync(
			"account_connection_test",
			Request(
				("account_id", settings.AccountId),
				("protocols", new object[] { "imap", "pop3", "smtp" })),
			cancellationToken: cancellation.Token), "account_connection_test");
		Assert.That(connections.GetArrayLength(), Is.EqualTo(3));
		foreach (var connection in connections.EnumerateArray())
		{
			TestContext.Out.WriteLine(
				$"connection {connection.GetProperty("protocol").GetString()}: "
				+ $"connected={connection.GetProperty("connected").GetBoolean()}, "
				+ $"tls={connection.GetProperty("tls_established").GetBoolean()}, "
				+ $"authenticated={connection.GetProperty("authenticated").GetBoolean()}");
			Assert.That(connection.GetProperty("connected").GetBoolean(), Is.True, connection.GetRawText());
			Assert.That(connection.GetProperty("authenticated").GetBoolean(), Is.True, connection.GetRawText());
		}

		// 2. folder_list and message_list on INBOX (read-only results).
		var folders = DataOf(await client.CallToolAsync(
			"folder_list",
			Request(("account_id", settings.AccountId)),
			cancellationToken: cancellation.Token), "folder_list");
		string? inboxId = folders.EnumerateArray()
			.Select(folder => folder.GetProperty("id").GetString())
			.FirstOrDefault(id => string.Equals(id, "INBOX", StringComparison.OrdinalIgnoreCase));
		Assert.That(inboxId, Is.Not.Null, "folder_list did not report an INBOX: " + folders.GetRawText());

		var envelopes = await CollectPagesAsync(
			client, "message_list",
			Request(("account_id", settings.AccountId), ("folder_id", inboxId!)),
			"messages", cancellation.Token);
		PrintEnvelopes("IMAP", envelopes);
		Dictionary<string, object?>? imapReference = null;
		if (settings.ImapUid is { } targetUid)
		{
			imapReference = ResolveReferenceByUid(envelopes, targetUid);
			TestContext.Out.WriteLine($"Selected IMAP reference for UID {targetUid}: {Serialize(imapReference)}");
		}
		else
		{
			TestContext.Out.WriteLine(
				$"IMAP read/mark/attachment steps skipped: set {ImapUidVariable} to a stable UID "
				+ "from the listing above after inspecting it.");
		}

		// 3. message_read(mark_as_read=false).
		if (imapReference is not null)
		{
			var read = DataOf(await client.CallToolAsync(
				"message_read",
				Request(
					("reference", imapReference),
					("mark_as_read", false)),
				cancellationToken: cancellation.Token), "message_read");
			Assert.Multiple(() =>
			{
				Assert.That(read.GetProperty("untrusted").GetBoolean(), Is.True);
				Assert.That(read.GetProperty("read_state_updated").GetBoolean(), Is.False,
					"message_read must not mark the message read when mark_as_read is false.");
			});
			PrintContent("IMAP", read);
		}

		// 4. pop3_message_list and pop3_message_read.
		var pop3Envelopes = await CollectPagesAsync(
			client, "pop3_message_list",
			Request(("account_id", settings.AccountId)),
			"messages", cancellation.Token);
		PrintEnvelopes("POP3", pop3Envelopes);
		if (settings.Pop3Uidl is not null)
		{
			Dictionary<string, object?> pop3Reference = ResolveReferenceByUidl(pop3Envelopes, settings.Pop3Uidl);
			TestContext.Out.WriteLine($"Selected POP3 reference for UIDL {settings.Pop3Uidl}.");
			var pop3Read = DataOf(await client.CallToolAsync(
				"pop3_message_read",
				Request(("reference", pop3Reference)),
				cancellationToken: cancellation.Token), "pop3_message_read");
			Assert.Multiple(() =>
			{
				Assert.That(pop3Read.GetProperty("untrusted").GetBoolean(), Is.True);
				Assert.That(pop3Read.GetProperty("read_state_supported").GetBoolean(), Is.False,
					"POP3 has no server-side read state.");
				Assert.That(pop3Read.GetProperty("is_read").ValueKind, Is.EqualTo(JsonValueKind.Null));
			});
			PrintContent("POP3", pop3Read);
		}
		else
		{
			TestContext.Out.WriteLine(
				$"POP3 read step skipped: set {Pop3UidlVariable} to a stable UIDL from the listing above.");
		}

		// 5. attachment_list; attachment_save only when a selected attachment ID is supplied.
		if (imapReference is not null)
		{
			var attachments = DataOf(await client.CallToolAsync(
				"attachment_list",
				Request(("reference", imapReference)),
				cancellationToken: cancellation.Token), "attachment_list");
			foreach (var attachment in attachments.EnumerateArray())
			{
				TestContext.Out.WriteLine(
					$"attachment {attachment.GetProperty("id").GetString()}: "
					+ $"{attachment.GetProperty("file_name").GetString()} "
					+ $"({attachment.GetProperty("content_type").GetString()})");
			}

			if (settings.AttachmentId is not null)
			{
				Assert.That(
					attachments.EnumerateArray()
						.Select(attachment => attachment.GetProperty("id").GetString()),
					Does.Contain(settings.AttachmentId),
					"The supplied attachment ID must appear in attachment_list output.");
				var saved = DataOf(await client.CallToolAsync(
					"attachment_save",
					Request(
						("reference", imapReference),
						("attachment_id", settings.AttachmentId)),
					cancellationToken: cancellation.Token), "attachment_save");
				string savedPath = saved.GetProperty("path").GetString()!;
				Assert.That(
					Path.GetFullPath(savedPath),
					Does.StartWith(Path.GetFullPath(dataDirectory)),
					"Saved attachments must stay inside the isolated data directory.");
				TestContext.Out.WriteLine($"Saved attachment {settings.AttachmentId} to {savedPath}.");
			}
			else
			{
				TestContext.Out.WriteLine(
					$"attachment_save skipped: set {AttachmentIdVariable} to a listed attachment ID.");
			}
		}

		// 6. message_mark_read only when MAILKIT_AGENT_LIVE_CONFIRM_MARK_READ=yes.
		if (settings.ConfirmMarkRead)
		{
			Assert.That(imapReference, Is.Not.Null,
				$"{ConfirmMarkReadVariable}=yes requires {ImapUidVariable} to select the message to mark.");
			var marked = DataOf(await client.CallToolAsync(
				"message_mark_read",
				Request(
					("references", new object[] { imapReference! }),
					("is_read", true)),
				cancellationToken: cancellation.Token), "message_mark_read");
			Assert.That(marked.GetInt32(), Is.EqualTo(1));
			TestContext.Out.WriteLine("Marked the selected IMAP message as read.");
		}
		else
		{
			TestContext.Out.WriteLine(
				$"message_mark_read skipped: set {ConfirmMarkReadVariable}=yes to mark the selected message.");
		}

		// 7. send_prepare only when a recipient is supplied.
		if (settings.Recipient is null)
		{
			TestContext.Out.WriteLine(
				$"send_prepare/send_commit skipped: set {RecipientVariable} to run the send phase.");
			AssertNoServerCrash(server);
			return;
		}

		string idempotencyKey = "live-"
			+ DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss")
			+ "-" + Guid.NewGuid().ToString("N")[..8];
		var preview = DataOf(await client.CallToolAsync(
			"send_prepare",
			Request(
				("account_id", settings.AccountId),
				("draft", new Dictionary<string, object?>
				{
					["to"] = new object[] { new Dictionary<string, object?> { ["address"] = settings.Recipient } },
					["subject"] = SendSubject,
					["text_body"] = SendTextBody
				}),
				("idempotency_key", idempotencyKey)),
			cancellationToken: cancellation.Token), "send_prepare");
		TestContext.Out.WriteLine("send_prepare preview (untrusted content):");
		TestContext.Out.WriteLine("  from:    " + GetStringOrNull(preview, "from"));
		TestContext.Out.WriteLine("  to:      " + string.Join(", ", EnumerateStrings(preview.GetProperty("to"))));
		TestContext.Out.WriteLine("  subject: " + GetStringOrNull(preview, "subject"));
		TestContext.Out.WriteLine("  preview: " + GetStringOrNull(preview, "text_preview"));
		TestContext.Out.WriteLine("  expires: " + preview.GetProperty("expires_at").GetString());

		// 8. send_commit only when MAILKIT_AGENT_LIVE_CONFIRM_SEND=yes.
		if (!settings.ConfirmSend)
		{
			TestContext.Out.WriteLine(
				"send_commit skipped: review the preview above, then re-run with the send confirmation "
				+ "switch (the wrapper requires typing SEND interactively before it sets "
				+ $"{ConfirmSendVariable}=yes).");
			AssertNoServerCrash(server);
			return;
		}

		string token = preview.GetProperty("confirmation_token").GetString()!;
		string messageId = preview.GetProperty("message_id").GetString()!;
		Assert.That(token, Is.Not.Null.And.Not.Empty);

		var committed = DataOf(await client.CallToolAsync(
			"send_commit",
			Request(("confirmation_token", token)),
			cancellationToken: cancellation.Token), "send_commit");
		Assert.That(committed.GetProperty("state").GetString(), Is.EqualTo("succeeded"), committed.GetRawText());
		DateTimeOffset attemptedAt = committed.GetProperty("attempted_at").GetDateTimeOffset();
		DateTimeOffset completedAt = committed.GetProperty("completed_at").GetDateTimeOffset();
		TestContext.Out.WriteLine($"send_commit succeeded: message_id={messageId}");

		// 9. repeat send_commit and assert no duplicate delivery: the one-time token is
		// consumed, so the repeat is rejected and the ledger still reports exactly one
		// succeeded delivery with unchanged timestamps and Message-Id.
		var repeated = await client.CallToolAsync(
			"send_commit",
			Request(("confirmation_token", token)),
			cancellationToken: cancellation.Token);
		Assert.That(repeated.StructuredContent!.Value.GetProperty("ok").GetBoolean(), Is.False,
			"A repeated send_commit with the same one-time token must be rejected.");

		var status = DataOf(await client.CallToolAsync(
			"send_status",
			Request(
				("account_id", settings.AccountId),
				("idempotency_key", idempotencyKey)),
			cancellationToken: cancellation.Token), "send_status");
		Assert.Multiple(() =>
		{
			Assert.That(status.GetProperty("state").GetString(), Is.EqualTo("succeeded"));
			Assert.That(status.GetProperty("message_id").GetString(), Is.EqualTo(messageId));
			Assert.That(status.GetProperty("attempted_at").GetDateTimeOffset(), Is.EqualTo(attemptedAt),
				"The idempotent send must not have been attempted a second time.");
			Assert.That(status.GetProperty("completed_at").GetDateTimeOffset(), Is.EqualTo(completedAt),
				"The delivery must not have completed a second time.");
		});
		TestContext.Out.WriteLine("Idempotency verified: no duplicate delivery for the repeated commit.");

		AssertNoServerCrash(server);
	}

	private static Dictionary<string, object?> Request(params (string Key, object? Value)[] fields)
	{
		var request = new Dictionary<string, object?>();
		foreach (var (key, value) in fields)
			request[key] = value;
		return new Dictionary<string, object?> { ["request"] = request };
	}

	private static Dictionary<string, object?> Endpoint(string host, int port, string tls) =>
		new()
		{
			["host"] = host,
			["port"] = port,
			["tls"] = tls
		};

	private static JsonElement DataOf(CallToolResult result, string toolName)
	{
		var root = result.StructuredContent
			?? throw new AssertionException(
				$"{toolName} returned no structured content: {JsonSerializer.Serialize(result.Content)}");
		Assert.That(
			root.GetProperty("ok").GetBoolean(),
			Is.True,
			$"{toolName} failed: {root.GetRawText()}");
		return root.GetProperty("data").Clone();
	}

	private static async Task<List<JsonElement>> CollectPagesAsync(
		McpClient client,
		string toolName,
		Dictionary<string, object?> requestTemplate,
		string arrayProperty,
		CancellationToken cancellationToken)
	{
		var items = new List<JsonElement>();
		string? cursor = null;
		for (var page = 0; page < MaxPages; page++)
		{
			var request = new Dictionary<string, object?>(requestTemplate)
			{
				["page_size"] = PageSize
			};
			if (cursor is not null)
				request["cursor"] = cursor;

			var data = DataOf(await client.CallToolAsync(
				toolName,
				new Dictionary<string, object?> { ["request"] = request },
				cancellationToken: cancellationToken), toolName);
			items.AddRange(data.GetProperty(arrayProperty).EnumerateArray().Select(item => item.Clone()));

			if (!data.TryGetProperty("next_cursor", out var next) ||
				next.ValueKind == JsonValueKind.Null ||
				string.IsNullOrEmpty(next.GetString()))
			{
				return items;
			}

			cursor = next.GetString();
		}

		return items;
	}

	private static Dictionary<string, object?> ResolveReferenceByUid(
		IReadOnlyList<JsonElement> envelopes,
		uint targetUid)
	{
		foreach (var envelope in envelopes)
		{
			var reference = envelope.GetProperty("reference");
			if (reference.TryGetProperty("uid", out var uid) &&
				uid.ValueKind != JsonValueKind.Null &&
				uid.GetUInt64() == targetUid)
			{
				return reference.Deserialize<Dictionary<string, object?>>()!;
			}
		}

		throw new AssertionException(
			$"UID {targetUid} was not found in the first {MaxPages * PageSize} listed messages. "
			+ "Inspect the printed listing and supply an existing stable UID.");
	}

	private static Dictionary<string, object?> ResolveReferenceByUidl(
		IReadOnlyList<JsonElement> envelopes,
		string targetUidl)
	{
		foreach (var envelope in envelopes)
		{
			var reference = envelope.GetProperty("reference");
			if (reference.TryGetProperty("uidl", out var uidl) &&
				string.Equals(uidl.GetString(), targetUidl, StringComparison.Ordinal))
			{
				return reference.Deserialize<Dictionary<string, object?>>()!;
			}
		}

		throw new AssertionException(
			$"UIDL '{targetUidl}' was not found in the listed POP3 messages. "
			+ "Inspect the printed listing and supply an existing stable UIDL.");
	}

	private static void PrintEnvelopes(string protocol, IReadOnlyList<JsonElement> envelopes)
	{
		TestContext.Out.WriteLine($"{protocol} listing ({envelopes.Count} messages):");
		foreach (var envelope in envelopes)
		{
			var reference = envelope.GetProperty("reference");
			string stableId = protocol == "IMAP"
				? "uid=" + (reference.TryGetProperty("uid", out var uid) ? uid.GetUInt64() : 0)
				: "uidl=" + (reference.TryGetProperty("uidl", out var uidl) ? uidl.GetString() : "?");
			TestContext.Out.WriteLine(
				$"  {stableId} date={GetStringOrNull(envelope, "date")} "
				+ $"from={JoinStrings(envelope, "from")} subject={GetStringOrNull(envelope, "subject")}");
		}
	}

	private static void PrintContent(string protocol, JsonElement content)
	{
		string text = content.GetProperty("text").GetString() ?? string.Empty;
		if (text.Length > PreviewLength)
			text = text[..PreviewLength] + "...";
		TestContext.Out.WriteLine(
			$"{protocol} read (untrusted content): subject={Header(content, "Subject")} text={text}");
	}

	private static string Header(JsonElement content, string name)
	{
		foreach (var header in content.GetProperty("headers").EnumerateArray())
		{
			if (string.Equals(header.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
				return header.GetProperty("value").GetString() ?? string.Empty;
		}

		return string.Empty;
	}

	private static string? GetStringOrNull(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
			? value.GetString()
			: null;

	private static IEnumerable<string?> EnumerateStrings(JsonElement array) =>
		array.ValueKind == JsonValueKind.Array
			? array.EnumerateArray().Select(item => item.GetString())
			: [];

	private static string JoinStrings(JsonElement element, string property) =>
		element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
			? string.Join(", ", value.EnumerateArray().Select(item => item.GetString()))
			: string.Empty;

	private static string Serialize(Dictionary<string, object?> value) =>
		JsonSerializer.Serialize(value);

	private static void AssertNoServerCrash(LiveStdioServer server)
	{
		string standardError = string.Join(Environment.NewLine, server.StandardError);
		Assert.That(standardError, Does.Not.Contain("Exception").IgnoreCase);
		Assert.That(standardError, Does.Not.Contain("Unhandled").IgnoreCase);
	}

	private static string ResolveServerAssembly()
	{
		var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory)
			.Parent?.Name ?? "Debug";
		return Path.Combine(
			FindRepositoryRoot(),
			"src",
			"MailKit.Agent.Mcp",
			"bin",
			configuration,
			"net8.0",
			"mailkit-agent.dll");
	}

	private static string FindRepositoryRoot()
	{
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		     directory is not null;
		     directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "MailKit.Agent.sln")))
				return directory.FullName;
		}

		throw new DirectoryNotFoundException("Could not find the repository root.");
	}

	private sealed record LiveSettings(
		string AccountId,
		string Username,
		string ImapHost,
		int ImapPort,
		string ImapTls,
		string Pop3Host,
		int Pop3Port,
		string Pop3Tls,
		string SmtpHost,
		int SmtpPort,
		string SmtpTls,
		string? DataDirectory,
		uint? ImapUid,
		string? Pop3Uidl,
		string? AttachmentId,
		string? Recipient,
		bool ConfirmMarkRead,
		bool ConfirmSend)
	{
		private static readonly string[] AllowedTlsModes = ["start_tls", "implicit_tls"];

		public static LiveSettings FromEnvironment()
		{
			string[] missing =
			[
				.. Required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
			];

			Assert.That(
				missing,
				Is.Empty,
				"Set the non-secret live settings before running this explicit fixture "
				+ $"(the wrapper scripts/Test-MailKitAgentLive.ps1 does this for you): {string.Join(", ", missing)}");

			return new LiveSettings(
				Environment.GetEnvironmentVariable(AccountIdVariable)!.Trim(),
				Environment.GetEnvironmentVariable(UsernameVariable)!.Trim(),
				Environment.GetEnvironmentVariable(ImapHostVariable)!.Trim(),
				Port(ImapPortVariable, 993),
				Tls(ImapTlsVariable),
				Environment.GetEnvironmentVariable(Pop3HostVariable)!.Trim(),
				Port(Pop3PortVariable, 995),
				Tls(Pop3TlsVariable),
				Environment.GetEnvironmentVariable(SmtpHostVariable)!.Trim(),
				Port(SmtpPortVariable, 465),
				Tls(SmtpTlsVariable),
				Optional(DataDirectoryVariable),
				uint.TryParse(Optional(ImapUidVariable), out var uid) ? uid : null,
				Optional(Pop3UidlVariable),
				Optional(AttachmentIdVariable),
				Optional(RecipientVariable),
				IsYes(ConfirmMarkReadVariable),
				IsYes(ConfirmSendVariable));
		}

		private static readonly string[] Required =
		[
			AccountIdVariable,
			UsernameVariable,
			ImapHostVariable,
			Pop3HostVariable,
			SmtpHostVariable
		];

		private static string? Optional(string name)
		{
			string? value = Environment.GetEnvironmentVariable(name);
			return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
		}

		private static int Port(string name, int fallback) =>
			int.TryParse(Optional(name), out var port) ? port : fallback;

		private static string Tls(string name)
		{
			string value = Optional(name) ?? DefaultTls;
			Assert.That(
				AllowedTlsModes,
				Does.Contain(value),
				$"{name} must be start_tls or implicit_tls; plain is rejected.");
			return value;
		}

		private static bool IsYes(string name) =>
			string.Equals(Optional(name), "yes", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// stdio harness for the live fixture. Unlike <see cref="StdioMcpServer"/>, it never
	/// activates <c>MAILKIT_AGENT_TEST_MODE</c>, accepts a caller-provided isolated data
	/// directory it does not own, and uses timeouts sized for real network servers. The
	/// spawned server resolves the account password itself through WindowsCredentialVault.
	/// </summary>
	private sealed class LiveStdioServer : IAsyncDisposable
	{
		private readonly ConcurrentQueue<string> _standardError;
		private readonly bool _ownsDataDirectory;

		private LiveStdioServer(
			McpClient client,
			string dataDirectory,
			bool ownsDataDirectory,
			ConcurrentQueue<string> standardError)
		{
			Client = client;
			DataDirectory = dataDirectory;
			_ownsDataDirectory = ownsDataDirectory;
			_standardError = standardError;
		}

		public McpClient Client { get; }

		public string DataDirectory { get; }

		public IReadOnlyCollection<string> StandardError => _standardError;

		public static async Task<LiveStdioServer> StartAsync(string dataDirectory, bool ownsDataDirectory)
		{
			var standardError = new ConcurrentQueue<string>();
			var transport = new StdioClientTransport(new StdioClientTransportOptions
			{
				Name = "MailKit Agent live smoke test",
				Command = DotnetHostResolver.Resolve(),
				Arguments = [ResolveServerAssembly()],
				WorkingDirectory = FindRepositoryRoot(),
				ShutdownTimeout = TimeSpan.FromSeconds(10),
				InheritEnvironmentVariables = false,
				EnvironmentVariables = CreateServerEnvironment(dataDirectory),
				StandardErrorLines = standardError.Enqueue
			});

			using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
			var client = await McpClient.CreateAsync(transport, cancellationToken: cancellation.Token);
			return new LiveStdioServer(client, dataDirectory, ownsDataDirectory, standardError);
		}

		public async ValueTask DisposeAsync()
		{
			try
			{
				await Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
			}
			finally
			{
				// A caller-supplied directory (the wrapper's) stays owned by the caller,
				// which validates the path before its own cleanup.
				if (_ownsDataDirectory && Directory.Exists(DataDirectory))
					Directory.Delete(DataDirectory, recursive: true);
			}
		}

		private static Dictionary<string, string?> CreateServerEnvironment(string dataDirectory)
		{
			var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
			environment["MAILKIT_AGENT_DATA_DIR"] = dataDirectory;
			environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
			environment["DOTNET_NOLOGO"] = "1";
			return environment;
		}
	}
}
