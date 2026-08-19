using System.Text;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Mail.Pop3;
using MailKit.Agent.Mail.Tests.ProtocolScripts;
using MailKit.Net.Pop3;
using MailKit.Security;

namespace MailKit.Agent.Mail.Tests.Pop3;

[TestFixture]
public sealed class Pop3GatewayTests
{
    private const string MessageText =
        "From: Alice <alice@example.test>\r\n" +
        "To: Bob <bob@example.test>\r\n" +
        "Subject: Replay message\r\n" +
        "Date: Tue, 19 Aug 2026 09:00:00 +0000\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "hello replay\r\n";

    private const string AttachmentMessageText =
        "From: Alice <alice@example.test>\r\n" +
        "To: Bob <bob@example.test>\r\n" +
        "Subject: Replay attachment\r\n" +
        "Date: Tue, 19 Aug 2026 09:00:00 +0000\r\n" +
        "MIME-Version: 1.0\r\n" +
        "Content-Type: multipart/mixed; boundary=boundary\r\n" +
        "\r\n" +
        "--boundary\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n" +
        "attachment body\r\n" +
        "--boundary\r\n" +
        "Content-Type: application/octet-stream\r\n" +
        "Content-Disposition: attachment; filename=note.bin\r\n" +
        "Content-Transfer-Encoding: base64\r\n" +
        "\r\n" +
        "QUJD\r\n" +
        "--boundary--\r\n";

    [Test]
    public async Task ListUsesUidlListAndTopToReturnStablePagedEnvelope()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 2,
            new("UIDL\r\n", Uidl("uidl-001", "uidl-002")),
            new("LIST\r\n", ListSizes(120, 240)),
            new("TOP 1 0\r\n", Headers("First"))));
        var gateway = CreateGateway(session);

        MessagePage page = await gateway.ListMessagesAsync(
            Profile, credential, offset: 0, pageSize: 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(page.Messages, Has.Count.EqualTo(1));
            Assert.That(page.Messages[0].Reference,
                Is.EqualTo(MessageReference.ForPop3("personal", "uidl-001")));
            Assert.That(page.Messages[0].Subject, Is.EqualTo("First"));
            Assert.That(page.Messages[0].From.Single(), Is.EqualTo("\"Alice\" <alice@example.test>"));
            Assert.That(page.Messages[0].To.Single(), Is.EqualTo("\"Bob\" <bob@example.test>"));
            Assert.That(page.Messages[0].Size, Is.EqualTo(120));
            Assert.That(page.Messages[0].Flags, Is.Empty);
            Assert.That(page.NextOffset, Is.EqualTo(1));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task ListFallsBackToRetrWhenTopIsNotAdvertised()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(ConversationWithoutTop(
            count: 1,
            new("UIDL\r\n", Uidl("uidl-001")),
            new("LIST\r\n", ListSizes(120)),
            new("RETR 1\r\n", Message(MessageText))));
        var gateway = CreateGateway(session);

        MessagePage page = await gateway.ListMessagesAsync(
            Profile, credential, offset: 0, pageSize: 1, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(page.Messages.Single().Reference,
                Is.EqualTo(MessageReference.ForPop3("personal", "uidl-001")));
            Assert.That(page.Messages.Single().Subject, Is.EqualTo("Replay message"));
            Assert.That(page.Messages.Single().InternalDate, Is.Null);
            Assert.That(page.Messages.Single().Flags, Is.Empty);
            Assert.That(page.Messages.Single().HasAttachments, Is.False);
        });
        session.AssertComplete();
    }

    [Test]
    public async Task ReadReloadsUidlsAndUsesRelocatedNumericIndex()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(
            Conversation(
                count: 1,
                new("UIDL\r\n", Uidl("uidl-001")),
                new("LIST\r\n", ListSizes(120)),
                new("TOP 1 0\r\n", Headers("First"))),
            Conversation(
                count: 2,
                new("UIDL\r\n", Uidl("uidl-new", "uidl-001")),
                new("RETR 2\r\n", Message(MessageText))));
        var gateway = CreateGateway(session);
        MessagePage page = await gateway.ListMessagesAsync(
            Profile, credential, 0, 25, CancellationToken.None);

        MessageContent content = await gateway.ReadAsync(
            Profile, credential, page.Messages[0].Reference,
            BodyMode.SafeText, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Is.EqualTo("hello replay\r\n"));
            Assert.That(content.ReadStateSupported, Is.False);
            Assert.That(content.IsRead, Is.Null);
            Assert.That(content.ReadStateUpdated, Is.False);
        });
        session.AssertComplete();
    }

    [Test]
    public void DuplicateUidlsRejectTheListingAsAConflict()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 2,
            new Pop3ReplayCommand("UIDL\r\n", Uidl("uidl-001", "uidl-001"))));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ListMessagesAsync(
                Profile, credential, 0, 25, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("pop3.uidl_conflict"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Conflict));
        });
        session.AssertComplete();
    }

    [Test]
    public void MissingUidlEntryRejectsTheListingAsAConflict()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 2,
            new Pop3ReplayCommand("UIDL\r\n", Uidl("uidl-001"))));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ListMessagesAsync(
                Profile, credential, 0, 25, CancellationToken.None))!;

        Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Conflict));
        session.AssertComplete();
    }

    [TestCase("1")]
    [TestCase("2 uidl-private-mailbox-text")]
    public void MalformedUidlResponseReturnsSanitizedStableConflict(string malformedEntry)
    {
        const string privateMarker = "private-mailbox-text";
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 1,
            new Pop3ReplayCommand(
                "UIDL\r\n",
                $"+OK {privateMarker}\r\n{malformedEntry}\r\n.\r\n")));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ListMessagesAsync(
                Profile, credential, 0, 25, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("pop3.uidl_conflict"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Conflict));
            Assert.That(exception.Error.Message, Does.Not.Contain(privateMarker));
            Assert.That(exception.Error.Details?.Values, Has.None.Contains(privateMarker));
        });
        session.AssertComplete();
    }

    [Test]
    public void MissingReferencedUidlReturnsStableReferenceConflict()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 1,
            new Pop3ReplayCommand("UIDL\r\n", Uidl("uidl-other"))));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ReadAsync(
                Profile, credential, MessageReference.ForPop3("personal", "uidl-missing"),
                BodyMode.SafeText, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("message.reference_conflict"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Conflict));
            Assert.That(exception.Error.Message, Does.Not.Contain("uidl-other"));
        });
        session.AssertComplete();
    }

    [Test]
    public void UidlCapabilityAbsentReturnsRequiredCapabilityWithoutInventingReferences()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(ConversationWithoutUidl(count: 1));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ListMessagesAsync(
                Profile, credential, 0, 25, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("pop3.uidl_required"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Capability));
        });
        session.AssertComplete();
    }

    [Test]
    public void AdvertisedUidlRejectedByServerReturnsRequiredCapability()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 1,
            new Pop3ReplayCommand("UIDL\r\n", "-ERR private UIDL denial\r\n")));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ListMessagesAsync(
                Profile, credential, 0, 25, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("pop3.uidl_required"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Capability));
            Assert.That(exception.Error.Message, Does.Not.Contain("private UIDL denial"));
        });
        session.AssertComplete();
    }

    [Test]
    public void ServerDisconnectReturnsSanitizedStableError()
    {
        const string privateMarker = "private-mailbox-text";
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(BuildConversation(
            count: 1,
            includeTop: true,
            includeQuit: false,
            new("UIDL\r\n", Uidl("uidl-001")),
            new("RETR 1\r\n", $"+OK {privateMarker}\r\n")));
        var gateway = CreateGateway(session);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await gateway.ReadAsync(
                Profile, credential, MessageReference.ForPop3("personal", "uidl-001"),
                BodyMode.SafeText, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Transient));
            Assert.That(exception.Error.Message, Does.Not.Contain(privateMarker));
            Assert.That(exception.Error.Details?.Values, Has.None.Contains(privateMarker));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task OpenAttachmentReturnsIndependentDecodedStreamAfterQuit()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var session = new ReplaySession(Conversation(
            count: 1,
            new("UIDL\r\n", Uidl("uidl-attachment")),
            new("RETR 1\r\n", Message(AttachmentMessageText))));
        var gateway = CreateGateway(session);

        await using Stream attachment = await gateway.OpenAttachmentAsync(
            Profile, credential, MessageReference.ForPop3("personal", "uidl-attachment"),
            "part-2", CancellationToken.None);
        session.AssertComplete();
        using var reader = new StreamReader(attachment, Encoding.ASCII);

        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("ABC"));
    }

    [Test]
    public void GatewayContractExposesOnlyPop3StableRetrievalOperations()
    {
        string[] methods = typeof(IPop3Gateway).GetMethods()
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(methods, Is.EqualTo(new[]
        {
            "ListMessagesAsync",
            "OpenAttachmentAsync",
            "ReadAsync"
        }));
    }

    [Test]
    public void FactoryRequiresConfiguredPop3Endpoint()
    {
        var profile = Profile with { Pop3 = null };
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var factory = new Pop3ClientFactory();

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await factory.CreateAsync(profile, credential, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("pop3.not_configured"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Capability));
        });
    }

    [Test]
    public void CallerCancellationPropagatesWithoutStableErrorWrapping()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var gateway = new Pop3Gateway(new CanceledFactory());

        Assert.That(async () => await gateway.ListMessagesAsync(
                Profile, credential, 0, 25, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static AccountProfile Profile { get; } = new(
        "personal", "Personal", "user", AuthenticationKind.Password,
        null, new EndpointSettings("pop3.example.test", 995, TlsMode.ImplicitTls), null);

    private static Pop3Gateway CreateGateway(ReplaySession session) =>
        new(session, commandTimeout: TimeSpan.FromSeconds(5), maxBodyCharacters: 10_000);

    private static IReadOnlyList<Pop3ReplayCommand> Conversation(
        int count,
        params Pop3ReplayCommand[] operationCommands) =>
        BuildConversation(count, includeTop: true, includeQuit: true, operationCommands);

    private static IReadOnlyList<Pop3ReplayCommand> ConversationWithoutTop(
        int count,
        params Pop3ReplayCommand[] operationCommands) =>
        BuildConversation(count, includeTop: false, includeQuit: true, operationCommands);

    private static IReadOnlyList<Pop3ReplayCommand> BuildConversation(
        int count,
        bool includeTop,
        bool includeQuit,
        params Pop3ReplayCommand[] operationCommands)
    {
        string capabilities =
            "+OK Capability list follows\r\n" +
            "USER\r\n" +
            (includeTop ? "TOP\r\n" : string.Empty) +
            "UIDL\r\n.\r\n";
        var commands = new List<Pop3ReplayCommand>
        {
            new("", "+OK replay ready\r\n"),
            new("CAPA\r\n", capabilities),
            new("USER user\r\n", "+OK user accepted\r\n"),
            new("PASS secret\r\n", "+OK authenticated\r\n"),
            new("CAPA\r\n", capabilities),
            new("STAT\r\n", $"+OK {count} {count * 120}\r\n")
        };
        commands.AddRange(operationCommands);
        if (includeQuit)
            commands.Add(new Pop3ReplayCommand("QUIT\r\n", "+OK goodbye\r\n"));
        return commands;
    }

    private static IReadOnlyList<Pop3ReplayCommand> ConversationWithoutUidl(int count)
    {
        const string capabilities =
            "+OK Capability list follows\r\n" +
            "USER\r\nTOP\r\n.\r\n";
        return new Pop3ReplayCommand[]
        {
            new("", "+OK replay ready\r\n"),
            new("CAPA\r\n", capabilities),
            new("USER user\r\n", "+OK user accepted\r\n"),
            new("PASS secret\r\n", "+OK authenticated\r\n"),
            new("CAPA\r\n", capabilities),
            new("STAT\r\n", $"+OK {count} {count * 120}\r\n"),
            new("UIDL 1\r\n", "-ERR UIDL unavailable\r\n"),
            new("QUIT\r\n", "+OK goodbye\r\n")
        };
    }

    private static string Uidl(params string[] uidls) =>
        "+OK UIDs follow\r\n" +
        string.Concat(uidls.Select((uidl, index) => $"{index + 1} {uidl}\r\n")) +
        ".\r\n";

    private static string ListSizes(params int[] sizes) =>
        "+OK sizes follow\r\n" +
        string.Concat(sizes.Select((size, index) => $"{index + 1} {size}\r\n")) +
        ".\r\n";

    private static string Headers(string subject) =>
        "+OK headers follow\r\n" +
        "From: Alice <alice@example.test>\r\n" +
        "To: Bob <bob@example.test>\r\n" +
        $"Subject: {subject}\r\n" +
        "Date: Tue, 19 Aug 2026 09:00:00 +0000\r\n" +
        "Content-Type: text/plain; charset=utf-8\r\n" +
        "\r\n.\r\n";

    private static string Message(string messageText) =>
        "+OK message follows\r\n" + messageText + ".\r\n";

    private sealed class ReplaySession : IPop3ClientFactory, IDisposable
    {
        private readonly Queue<IReadOnlyList<Pop3ReplayCommand>> conversations;
        private readonly List<Pop3Client> clients = [];
        private readonly List<Pop3ReplayStream> streams = [];

        public ReplaySession(params IReadOnlyList<Pop3ReplayCommand>[] conversations)
        {
            this.conversations = new Queue<IReadOnlyList<Pop3ReplayCommand>>(conversations);
        }

        public async Task<Pop3Client> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken)
        {
            Assert.That(conversations, Is.Not.Empty, "Gateway requested an unexpected POP3 session.");
            var stream = new Pop3ReplayStream(conversations.Dequeue());
            var client = new Pop3Client();
            streams.Add(stream);
            clients.Add(client);

            await client.ConnectAsync(stream, "pop3.example.test", 110,
                SecureSocketOptions.None, cancellationToken);
            await client.AuthenticateAsync(
                credential.CreateNetworkCredential(profile.Username), cancellationToken);
            return client;
        }

        public void AssertComplete()
        {
            Assert.That(conversations, Is.Empty, "Not every expected POP3 session was opened.");
            Assert.Multiple(() =>
            {
                foreach (Pop3ReplayStream stream in streams)
                    stream.AssertComplete();
            });
        }

        public void Dispose()
        {
            foreach (Pop3Client client in clients)
                client.Dispose();
            foreach (Pop3ReplayStream stream in streams)
                stream.Dispose();
        }
    }

    private sealed class CanceledFactory : IPop3ClientFactory
    {
        public Task<Pop3Client> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<Pop3Client>(cancellationToken);
    }
}
