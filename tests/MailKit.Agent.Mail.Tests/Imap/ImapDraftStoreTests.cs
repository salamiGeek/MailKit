using System.Reflection;
using System.Text;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Imap;
using MailKit.Agent.Mail.Tests.ProtocolScripts;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace MailKit.Agent.Mail.Tests.Imap;

[TestFixture]
public sealed class ImapDraftStoreTests
{
	private static readonly DateTimeOffset PreparedAt =
		new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);

	// Deliberately simple, CRLF-canonical MIME so the APPEND literal is the exact
	// prepared bytes re-serialized with the append formatting options.
	private const string MimeText =
		"From: user@example.test\r\n" +
		"To: alice@example.test\r\n" +
		"Subject: Draft replay\r\n" +
		"\r\n" +
		"draft body\r\n";

	[Test]
	public async Task SpecialUseDraftsFolderAppendsWithDraftFlagAndExactLiteral()
	{
		using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
		byte[] literal = SerializeForAppend(Encoding.UTF8.GetBytes(MimeText));
		using var session = NewSession(
			specialUse: true,
			specialUseList: "* LIST (\\HasNoChildren \\Drafts) \"/\" Drafts\r\n",
			[.. AppendCommands("A00000005", "Drafts", literal), LogoutCommand("A00000006")]);		var store = new ImapDraftStore(session, commandTimeout: TimeSpan.FromSeconds(5));

		SendTransportOutcome outcome = await store.SaveAsync(
			Profile, credential, PreparedMessage(), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.State, Is.EqualTo(SendState.Succeeded),
				"The APPEND completed: " + outcome.Error?.Message);
			Assert.That(literal, Is.EqualTo(Encoding.UTF8.GetBytes(MimeText)),
				"The CRLF-canonical prepared bytes must be preserved on the wire.");
		});
		session.AssertComplete();
	}

	[Test]
	public async Task NameFallbackScansPersonalNamespaceWhenSpecialUseIsUnadvertised()
	{
		using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
		byte[] literal = SerializeForAppend(Encoding.UTF8.GetBytes(MimeText));
		using var session = NewSession(
			specialUse: false,
			specialUseList: string.Empty,
			[new ImapReplayCommand(
				"A00000004 LIST \"\" \"*\" RETURN (SUBSCRIBED CHILDREN)\r\n",
				"* LIST (\\HasNoChildren \\Inbox) \"/\" INBOX\r\n" +
				"* LIST (\\HasNoChildren) \"/\" Drafts\r\n" +
				"A00000004 OK LIST completed\r\n"),
			.. AppendCommands("A00000005", "Drafts", literal),
			LogoutCommand("A00000006")]);
		var store = new ImapDraftStore(session, commandTimeout: TimeSpan.FromSeconds(5));

		SendTransportOutcome outcome = await store.SaveAsync(
			Profile, credential, PreparedMessage(), CancellationToken.None);

		Assert.That(outcome.State, Is.EqualTo(SendState.Succeeded),
			"The APPEND completed: " + outcome.Error?.Message);
		session.AssertComplete();
	}

	[Test]
	public async Task NameFallbackMatchesTheQqChineseDraftsFolderName()
	{
		// QQ IMAP advertises neither SPECIAL-USE nor an ASCII "Drafts" folder; its
		// drafts folder is the modified-UTF-7 name "&g0l6P3ux-" (草稿箱).
		using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
		byte[] literal = SerializeForAppend(Encoding.UTF8.GetBytes(MimeText));
		using var session = NewSession(
			specialUse: false,
			specialUseList: string.Empty,
			[new ImapReplayCommand(
				"A00000004 LIST \"\" \"*\" RETURN (SUBSCRIBED CHILDREN)\r\n",
				"* LIST (\\HasNoChildren \\Inbox) \"/\" INBOX\r\n" +
				"* LIST (\\HasNoChildren) \"/\" \"&g0l6P3ux-\"\r\n" +
				"A00000004 OK LIST completed\r\n"),
			.. AppendCommands("A00000005", "&g0l6P3ux-", literal),
			LogoutCommand("A00000006")]);
		var store = new ImapDraftStore(session, commandTimeout: TimeSpan.FromSeconds(5));

		SendTransportOutcome outcome = await store.SaveAsync(
			Profile, credential, PreparedMessage(), CancellationToken.None);

		Assert.That(outcome.State, Is.EqualTo(SendState.Succeeded),
			"The APPEND targeted the QQ drafts folder: " + outcome.Error?.Message);
		session.AssertComplete();
	}

	[Test]
	public async Task MissingDraftsFolderFailsWithStableSanitizedCapabilityError()
	{
		using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
		using var session = NewSession(
			specialUse: false,
			specialUseList: string.Empty,
			[new ImapReplayCommand(
				"A00000004 LIST \"\" \"*\" RETURN (SUBSCRIBED CHILDREN)\r\n",
				"* LIST (\\HasNoChildren \\Inbox) \"/\" INBOX\r\n" +
				"A00000004 OK LIST completed\r\n"),
				LogoutCommand("A00000005")]);
		var store = new ImapDraftStore(session, commandTimeout: TimeSpan.FromSeconds(5));

		SendTransportOutcome outcome = await store.SaveAsync(
			Profile, credential, PreparedMessage(), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
			Assert.That(outcome.Error!.Code, Is.EqualTo("drafts.folder_not_found"));
			Assert.That(outcome.Error.Category, Is.EqualTo(ErrorCategory.Capability));
			Assert.That(outcome.Error.Message, Does.Not.Contain("imap.example.test"));
		});
		session.AssertComplete();
	}

	[Test]
	public async Task DisconnectDuringAppendIsIndeterminateAndNeverRetried()
	{
		using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
		byte[] literal = SerializeForAppend(Encoding.UTF8.GetBytes(MimeText));
		// The scripted APPEND never completes: the server sends the literal
		// continuation, accepts the bytes, and then drops the connection without a
		// tagged response, so the append outcome is unknown and must be reported as
		// Indeterminate rather than retried.
		using var session = NewSession(
			specialUse: true,
			specialUseList: "* LIST (\\HasNoChildren \\Drafts) \"/\" Drafts\r\n",
			[new ImapReplayCommand(
				"A00000005 APPEND Drafts (\\Draft) \"19-Aug-2026 04:00:00 +0000\" {" +
				literal.Length + "}\r\n",
				"+ Ready for literal data\r\n"),
			new ImapReplayCommand(Latin1(literal) + "\r\n", string.Empty)]);
		var store = new ImapDraftStore(session, commandTimeout: TimeSpan.FromSeconds(5));

		SendTransportOutcome outcome = await store.SaveAsync(
			Profile, credential, PreparedMessage(), CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(outcome.State, Is.EqualTo(SendState.Indeterminate));
			Assert.That(outcome.Error!.Code, Is.EqualTo("drafts.transport_unknown"));
		});
		session.AssertComplete();
	}

	private static AccountProfile Profile { get; } = new(
		"account-one", "Account One", "user", AuthenticationKind.Password,
		new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls), null, null);

	/// <summary>
	/// The exact wire commands of one successful APPEND: MailKit emits the command
	/// prefix (folder name as an atom, the (\Draft) flag list, the prepared date)
	/// ending in <c>{N}\r\n</c>, waits for the continuation, then writes the literal
	/// bytes followed by the command-terminating CRLF.
	/// </summary>
	private static ImapReplayCommand[] AppendCommands(
		string tag, string encodedFolderName, byte[] literal) =>
	[
		new ImapReplayCommand(
			tag + " APPEND " + encodedFolderName +
			" (\\Draft) \"19-Aug-2026 04:00:00 +0000\" {" + literal.Length + "}\r\n",
			"+ Ready for literal data\r\n"),
		new ImapReplayCommand(
			Latin1(literal) + "\r\n",
			tag + " OK APPEND completed\r\n")
	];

	private static ImapReplayCommand LogoutCommand(string tag) => new(
		tag + " LOGOUT\r\n",
		"* BYE logging out\r\n" + tag + " OK LOGOUT completed\r\n");

	private static PreparedOutgoingMessage PreparedMessage()
	{
		return new PreparedOutgoingMessage(
			"prep-1",
			"personal",
			"<id-1@mailkit-agent.local>",
			new string('a', 64),
			Encoding.UTF8.GetBytes(MimeText),
			"user@example.test",
			["alice@example.test"],
			new SendPreview(
				"prep-1",
				"personal",
				"<id-1@mailkit-agent.local>",
				null,
				[],
				[],
				[],
				SendMode.Drafts,
				"Draft replay",
				null,
				0,
				[],
				PreparedAt,
				PreparedAt.AddMinutes(10),
				new string('c', 64),
				new string('d', 64),
				"token"),
			new string('b', 64),
			PreparedAt.AddMinutes(10));
	}

	/// <summary>
	/// Re-serializes the prepared MIME exactly the way MailKit formats an APPEND
	/// literal (DOS newlines, trailing newline ensured), so the tests can assert the
	/// byte-exact wire command including the literal payload.
	/// </summary>
	private static byte[] SerializeForAppend(byte[] preparedMime)
	{
		using var input = new MemoryStream(preparedMime, writable: false);
		MimeMessage message = MimeMessage.LoadAsync(input, CancellationToken.None)
			.GetAwaiter().GetResult();
		FormatOptions options = FormatOptions.Default.Clone();
		options.NewLineFormat = NewLineFormat.Dos;
		options.EnsureNewLine = true;
		using var output = new MemoryStream();
		message.WriteTo(options, output, CancellationToken.None);
		return output.ToArray();
	}

	private static string Latin1(byte[] bytes) =>
		Encoding.GetEncoding(28591).GetString(bytes);

	private static ReplaySession NewSession(
		bool specialUse,
		string specialUseList,
		IReadOnlyList<ImapReplayCommand> operationCommands) =>
		new(specialUse, specialUseList, operationCommands);

	/// <summary>
	/// The house ImapReplayStream fixture specialized for the draft store: the
	/// connect handshake advertises SPECIAL-USE only when the scripted server
	/// supports it, so the special-use and name-fallback paths are both reachable.
	/// Operation commands must be pre-tagged by the caller (A00000004 without
	/// SPECIAL-USE, A00000005 with it).
	/// </summary>
	private sealed class ReplaySession : IImapClientFactory, IDisposable
	{
		private readonly ImapReplayStream stream;
		private readonly ImapClient client = new();
		private bool initialized;

		public ReplaySession(
			bool specialUse,
			string specialUseList,
			IReadOnlyList<ImapReplayCommand> operationCommands)
		{
			typeof(ImapClient)
				.GetProperty("TagPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
				.SetValue(client, 'A');
			string capability =
				"* CAPABILITY IMAP4rev1 NAMESPACE LIST-EXTENDED ESEARCH AUTH=PLAIN" +
				(specialUse ? " SPECIAL-USE" : string.Empty) + "\r\n";
			var commands = new List<ImapReplayCommand>
			{
				new("", "* OK replay ready\r\n"),
				new("A00000000 CAPABILITY\r\n",
					capability + "A00000000 OK CAPABILITY completed\r\n"),
				new("A00000001 AUTHENTICATE PLAIN\r\n", "+\r\n"),
				new("AHVzZXIAc2VjcmV0\r\n",
					capability + "A00000001 OK AUTHENTICATE completed\r\n"),
				new("A00000002 NAMESPACE\r\n",
					"* NAMESPACE ((\"\" \"/\")) NIL NIL\r\nA00000002 OK NAMESPACE completed\r\n"),
				new("A00000003 LIST \"\" \"INBOX\" RETURN (SUBSCRIBED CHILDREN)\r\n",
					"* LIST (\\HasNoChildren \\Inbox) \"/\" INBOX\r\n" +
					"A00000003 OK LIST completed\r\n")
			};
			if (specialUse)
			{
				commands.Add(new ImapReplayCommand(
					"A00000004 LIST (SPECIAL-USE) \"\" \"*\" RETURN (SUBSCRIBED CHILDREN)\r\n",
					specialUseList + "A00000004 OK LIST completed\r\n"));
			}

			commands.AddRange(operationCommands);
			stream = new ImapReplayStream(commands);
		}

		public async Task<ImapClient> CreateAsync(
			AccountProfile profile,
			PasswordCredentialLease credential,
			CancellationToken cancellationToken)
		{
			if (!initialized)
			{
				initialized = true;
				await client.ConnectAsync(stream, "imap.example.test", 143,
					SecureSocketOptions.None, cancellationToken);
				var networkCredential = credential.CreateNetworkCredential(profile.Username);
				await client.AuthenticateAsync(networkCredential, cancellationToken);
			}

			return client;
		}

		public void AssertComplete() => stream.AssertComplete();

		public void Dispose()
		{
			client.Dispose();
			stream.Dispose();
		}
	}
}
