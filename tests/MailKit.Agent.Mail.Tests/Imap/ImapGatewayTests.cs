using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Mail.Imap;
using MailKit.Agent.Mail.Tests.ProtocolScripts;
using MailKit.Net.Imap;
using MailKit.Security;
using System.Reflection;
using System.Text;

namespace MailKit.Agent.Mail.Tests.Imap;

[TestFixture]
public sealed class ImapGatewayTests
{
    private const uint ExpectedUidValidity = 777;
    private const string MessageText = "From: sender@example.test\r\n" +
        "To: reader@example.test\r\n" +
        "Subject: Replay body\r\n" +
        "Content-Type: multipart/mixed; boundary=agent\r\n\r\n" +
        "--agent\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nhello replay\r\n" +
        "--agent\r\nContent-Type: application/octet-stream\r\n" +
        "Content-Disposition: attachment; filename=note.bin\r\n\r\nABC\r\n" +
        "--agent--\r\n";

    [Test]
    public async Task ListFoldersDiscoversSelectableAndSpecialUseFolders()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            new ImapReplayCommand("A00000005 LIST \"\" \"*\" RETURN (SUBSCRIBED CHILDREN)\r\n",
                "* LIST (\\HasNoChildren \\Inbox) \"/\" INBOX\r\n" +
                "* LIST (\\HasNoChildren \\Sent) \"/\" Sent\r\n" +
                "A00000005 OK LIST completed\r\n"));
        var gateway = CreateGateway(session);

        IReadOnlyList<FolderDescriptor> folders = await gateway.ListFoldersAsync(
            Profile, credential, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(folders.Select(folder => folder.Id), Is.EqualTo(new[] { "INBOX", "Sent" }));
            Assert.That(folders[0].IsSelectable, Is.True);
            Assert.That(folders[1].SpecialUse, Is.EqualTo("sent"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task ListMessagesPagesNewestUidsFirstAndReturnsStableReferences()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Select("A00000005", ExpectedUidValidity, 3),
            new("A00000006 UID SEARCH RETURN (ALL) ALL\r\n",
                "* ESEARCH (TAG \"A00000006\") UID ALL 10,30,20\r\nA00000006 OK SEARCH completed\r\n"),
            FetchSummaries("A00000007", "30,20"));
        var gateway = CreateGateway(session);

        MessagePage page = await gateway.ListMessagesAsync(
            Profile, credential, "INBOX", offset: 0, pageSize: 2, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(page.Messages.Select(message => message.Reference.Uid), Is.EqualTo(new uint?[] { 30, 20 }));
            Assert.That(page.NextOffset, Is.EqualTo(2));
            Assert.That(page.Messages[0].Reference, Is.EqualTo(
                MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 30)));
            Assert.That(page.Messages[0].HasAttachments, Is.True);
        });
        session.AssertComplete();
    }

    [Test]
    public async Task SearchBuildsOnlyTypedCriteriaAndPagesStableResults()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Examine("A00000005", ExpectedUidValidity, 2),
            new("A00000006 UID SEARCH RETURN (ALL) TEXT report FROM alice@example.test TO bob@example.test SUBJECT quarterly SINCE 1-Aug-2026 BEFORE 19-Aug-2026 UNSEEN\r\n",
                "* ESEARCH (TAG \"A00000006\") UID ALL 41,42\r\nA00000006 OK SEARCH completed\r\n"),
            FetchSummaries("A00000007", "42"));
        var gateway = CreateGateway(session);
        var criteria = new MessageSearchCriteria
        {
            Text = "report",
            From = "alice@example.test",
            To = "bob@example.test",
            Subject = "quarterly",
            Since = new DateTime(2026, 8, 1),
            Before = new DateTime(2026, 8, 19),
            Unread = true
        };

        MessagePage page = await gateway.SearchAsync(
            Profile, credential, "INBOX", criteria, offset: 0, pageSize: 1,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(page.Messages.Single().Reference.Uid, Is.EqualTo(42));
            Assert.That(page.NextOffset, Is.EqualTo(1));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task ReadWithoutMarkingUsesPeekAndEmitsNoStore()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Examine("A00000005", ExpectedUidValidity, 1),
            FetchMessage("A00000006", 42, peek: true));
        var gateway = CreateGateway(session);

        MessageContent content = await gateway.ReadAsync(
            Profile, credential,
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
            markAsRead: false, BodyMode.SafeText, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Is.EqualTo("hello replay"));
            Assert.That(content.ReadStateSupported, Is.True);
            Assert.That(content.ReadStateUpdated, Is.False);
            Assert.That(content.Attachments.Single().Id, Is.EqualTo("part-2"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task DefaultReadExplicitlyEnsuresSeenAfterBodyFetch()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Select("A00000005", ExpectedUidValidity, 1),
            FetchMessage("A00000006", 42, peek: true),
            new("A00000007 UID STORE 42 +FLAGS.SILENT (\\Seen)\r\n",
                "A00000007 OK STORE completed\r\n"));
        var gateway = CreateGateway(session);

        MessageContent content = await gateway.ReadAsync(
            Profile, credential,
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
            markAsRead: true, BodyMode.SafeText, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Is.EqualTo("hello replay"));
            Assert.That(content.IsRead, Is.True);
            Assert.That(content.ReadStateUpdated, Is.True);
            Assert.That(content.Warnings, Is.Empty);
        });
        session.AssertComplete();
    }

    [Test]
    public void ReadRejectsUidValidityMismatchBeforeFetchingBody()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Examine("A00000005", 778, 1));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ReadAsync(
                Profile, credential,
                MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
                markAsRead: false, BodyMode.SafeText, CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("message.reference_conflict"));
        session.AssertComplete();
    }

    [Test]
    public async Task ReadReturnsBodyAndWarningWhenSeenUpdateIsDenied()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Select("A00000005", ExpectedUidValidity, 1),
            FetchMessage("A00000006", 42, peek: true),
            new("A00000007 UID STORE 42 +FLAGS.SILENT (\\Seen)\r\n",
                "A00000007 NO [NOPERM] PRIVATE-SERVER-MARKER\r\n"));
        var gateway = CreateGateway(session);

        MessageContent content = await gateway.ReadAsync(
            Profile, credential,
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
            markAsRead: true, BodyMode.SafeText, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Is.EqualTo("hello replay"));
            Assert.That(content.ReadStateUpdated, Is.False);
            Assert.That(content.Warnings.Single().Code, Is.EqualTo("imap.seen_update_failed"));
            Assert.That(content.Warnings.Single().Message, Does.Not.Contain("PRIVATE-SERVER-MARKER"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task ReadOnlyFolderStillReturnsBodyWithSeenWarning()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            SelectReadOnly("A00000005", ExpectedUidValidity, 1),
            FetchMessage("A00000006", 42, peek: true));
        var gateway = CreateGateway(session);

        MessageContent content = await gateway.ReadAsync(
            Profile, credential,
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
            markAsRead: true, BodyMode.SafeText, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Is.EqualTo("hello replay"));
            Assert.That(content.ReadStateUpdated, Is.False);
            Assert.That(content.Warnings.Single().Code, Is.EqualTo("imap.seen_update_failed"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task MarkReadBatchesStableUidsAndExplicitlyRemovesSeen()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Select("A00000005", ExpectedUidValidity, 2),
            new("A00000006 UID STORE 41:42 -FLAGS.SILENT (\\Seen)\r\n",
                "A00000006 OK STORE completed\r\n"));
        var gateway = CreateGateway(session);
        MessageReference[] references =
        {
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 41)
        };

        int updated = await gateway.MarkReadAsync(
            Profile, credential, references, isRead: false, CancellationToken.None);

        Assert.That(updated, Is.EqualTo(2));
        session.AssertComplete();
    }

    [Test]
    public async Task OpenAttachmentReturnsDecodedTraversalPartWithoutChangingSeen()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Examine("A00000005", ExpectedUidValidity, 1),
            FetchMessage("A00000006", 42, peek: true));
        var gateway = CreateGateway(session);

        await using Stream attachment = await gateway.OpenAttachmentAsync(
            Profile, credential,
            MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42),
            "part-2", CancellationToken.None);
        using var reader = new StreamReader(attachment, Encoding.ASCII);

        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("ABC"));
        session.AssertComplete();
    }

    [Test]
    public void MarkReadRejectsChangedUidValidityBeforeStore()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Select("A00000005", 778, 1));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.MarkReadAsync(
                Profile, credential,
                new[] { MessageReference.ForImap("account-one", "INBOX", ExpectedUidValidity, 42) },
                isRead: true, CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("message.reference_conflict"));
        session.AssertComplete();
    }

    [Test]
    public void ListMessagesRequiresPositiveUidValidity()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Select("A00000005", 0, 1));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ListMessagesAsync(
                Profile, credential, "INBOX", offset: 0, pageSize: 10,
                CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("imap.uidvalidity_unavailable"));
        session.AssertComplete();
    }

    [Test]
    public void CallerCancellationPropagatesWithoutStableErrorWrapping()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var gateway = new ImapGateway(new CanceledFactory());

        Assert.That(async () => await gateway.ListFoldersAsync(
                Profile, credential, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static AccountProfile Profile { get; } = new(
        "account-one", "Account One", "user", AuthenticationKind.Password,
        new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls), null, null);

    private static ImapGateway CreateGateway(ReplaySession session) =>
        new(session, commandTimeout: TimeSpan.FromSeconds(5), maxBodyCharacters: 10_000);

    private static ImapReplayCommand Select(string tag, uint uidValidity, int exists) =>
        new($"{tag} SELECT INBOX\r\n",
            $"* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)\r\n" +
            $"* {exists} EXISTS\r\n* OK [UIDVALIDITY {uidValidity}] UIDs valid\r\n" +
            $"{tag} OK [READ-WRITE] SELECT completed\r\n");

    private static ImapReplayCommand Examine(string tag, uint uidValidity, int exists) =>
        new($"{tag} EXAMINE INBOX\r\n",
            $"* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)\r\n" +
            $"* {exists} EXISTS\r\n* OK [UIDVALIDITY {uidValidity}] UIDs valid\r\n" +
            $"{tag} OK [READ-ONLY] EXAMINE completed\r\n");

    private static ImapReplayCommand SelectReadOnly(string tag, uint uidValidity, int exists) =>
        new($"{tag} SELECT INBOX\r\n",
            $"* FLAGS (\\Seen \\Answered \\Flagged \\Deleted \\Draft)\r\n" +
            $"* {exists} EXISTS\r\n* OK [UIDVALIDITY {uidValidity}] UIDs valid\r\n" +
            $"{tag} OK [READ-ONLY] SELECT completed\r\n");

    private static ImapReplayCommand FetchSummaries(string tag, string uids)
    {
        string response = uids == "42"
            ? "* 1 FETCH (UID 42 FLAGS () INTERNALDATE \"19-Aug-2026 09:00:00 +0000\" RFC822.SIZE 240 " +
              "ENVELOPE (\"Tue, 19 Aug 2026 09:00:00 +0000\" \"Quarterly\" ((\"Alice\" NIL \"alice\" \"example.test\")) " +
              "NIL NIL ((\"Bob\" NIL \"bob\" \"example.test\")) NIL NIL NIL \"<quarterly@example.test>\") " +
              "BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 4 1))\r\n"
            : "* 1 FETCH (UID 20 FLAGS (\\Seen) INTERNALDATE \"18-Aug-2026 08:00:00 +0000\" RFC822.SIZE 120 " +
            "ENVELOPE (\"Mon, 18 Aug 2026 08:00:00 +0000\" \"Older\" ((\"Alice\" NIL \"alice\" \"example.test\")) " +
            "NIL NIL ((\"Bob\" NIL \"bob\" \"example.test\")) NIL NIL NIL \"<older@example.test>\") " +
            "BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 4 1))\r\n" +
            "* 2 FETCH (UID 30 FLAGS () INTERNALDATE \"19-Aug-2026 08:00:00 +0000\" RFC822.SIZE 240 " +
            "ENVELOPE (\"Tue, 19 Aug 2026 08:00:00 +0000\" \"Newest\" ((\"Carol\" NIL \"carol\" \"example.test\")) " +
            "NIL NIL ((\"Bob\" NIL \"bob\" \"example.test\")) NIL NIL NIL \"<newest@example.test>\") " +
            "BODYSTRUCTURE ((\"TEXT\" \"PLAIN\" (\"CHARSET\" \"UTF-8\") NIL NIL \"7BIT\" 4 1) " +
            "(\"APPLICATION\" \"OCTET-STREAM\" (\"NAME\" \"note.bin\") NIL NIL \"BASE64\" 4 NIL (\"ATTACHMENT\" (\"FILENAME\" \"note.bin\"))) \"MIXED\"))\r\n";

        return new ImapReplayCommand(
            $"{tag} UID FETCH {uids} (UID FLAGS INTERNALDATE RFC822.SIZE ENVELOPE BODYSTRUCTURE)\r\n",
            response + $"{tag} OK FETCH completed\r\n");
    }

    private static ImapReplayCommand FetchMessage(string tag, uint uid, bool peek)
    {
        string item = peek ? "BODY.PEEK[]" : "BODY[]";
        return new($"{tag} UID FETCH {uid} ({item})\r\n",
            $"* 1 FETCH (UID {uid} BODY[] {{{Encoding.ASCII.GetByteCount(MessageText)}}}\r\n" +
            MessageText + $")\r\n{tag} OK FETCH completed\r\n");
    }

    private sealed class ReplaySession : IImapClientFactory, IDisposable
    {
        private readonly ImapReplayStream stream;
        private readonly ImapClient client = new();
        private bool initialized;

        public ReplaySession(params ImapReplayCommand[] operationCommands)
        {
            typeof(ImapClient)
                .GetProperty("TagPrefix", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(client, 'A');
            var commands = new List<ImapReplayCommand>
            {
                new("", "* OK replay ready\r\n"),
                new("A00000000 CAPABILITY\r\n",
                    "* CAPABILITY IMAP4rev1 NAMESPACE SPECIAL-USE LIST-EXTENDED ESEARCH AUTH=PLAIN\r\n" +
                    "A00000000 OK CAPABILITY completed\r\n"),
                new("A00000001 AUTHENTICATE PLAIN\r\n", "+\r\n"),
                new("AHVzZXIAc2VjcmV0\r\n",
                    "* CAPABILITY IMAP4rev1 NAMESPACE SPECIAL-USE LIST-EXTENDED ESEARCH AUTH=PLAIN\r\n" +
                    "A00000001 OK AUTHENTICATE completed\r\n"),
                new("A00000002 NAMESPACE\r\n",
                    "* NAMESPACE ((\"\" \"/\")) NIL NIL\r\nA00000002 OK NAMESPACE completed\r\n"),
                new("A00000003 LIST \"\" \"INBOX\" RETURN (SUBSCRIBED CHILDREN)\r\n",
                    "* LIST (\\HasNoChildren \\Inbox) \"/\" INBOX\r\nA00000003 OK LIST completed\r\n"),
                new("A00000004 LIST (SPECIAL-USE) \"\" \"*\" RETURN (SUBSCRIBED CHILDREN)\r\n",
                    "* LIST (\\HasNoChildren \\Sent) \"/\" Sent\r\nA00000004 OK LIST completed\r\n")
            };
            commands.AddRange(operationCommands);
            string logoutTag = $"A{5 + operationCommands.Length:D8}";
            commands.Add(new ImapReplayCommand(
                $"{logoutTag} LOGOUT\r\n",
                $"* BYE logging out\r\n{logoutTag} OK LOGOUT completed\r\n"));
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

    private sealed class CanceledFactory : IImapClientFactory
    {
        public Task<ImapClient> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<ImapClient>(cancellationToken);
    }
}
