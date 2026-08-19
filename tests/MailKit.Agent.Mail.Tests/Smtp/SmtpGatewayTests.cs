using System.Text;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Smtp;
using MailKit.Agent.Mail.Tests.ProtocolScripts;
using MailKit.Net.Smtp;
using MailKit.Security;

namespace MailKit.Agent.Mail.Tests.Smtp;

[TestFixture]
public sealed class SmtpGatewayTests
{
    private const string SecretServerMarker = "private-server-marker";
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task SuccessfulSendDeliversBccOnlyThroughEnvelopeWithSingleDataCommand()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<hidden@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("DATA\r\n", "354 go ahead\r\n"),
            new SmtpReplayCommand(".\r\n", "250 accepted\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message("alice@example.test", "hidden@example.test"),
            CancellationToken.None);

        SmtpReplayStream stream = session.SingleStream;
        string payload = Encoding.UTF8.GetString(stream.DataPayloads.Single());
        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(stream.DataCommandCount, Is.EqualTo(1),
                "A single send attempt must issue exactly one DATA command.");
            Assert.That(payload, Does.Contain("Message-Id: <id-1@mailkit-agent.local>"));
            Assert.That(payload, Does.Not.Contain("Bcc"),
                "The DATA payload must never contain Bcc information.");
            Assert.That(payload, Does.Contain("alice@example.test"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task RecipientRejectionReturnsFailedWithSanitizedError()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", $"550 {SecretServerMarker} no such mailbox\r\n"),
            new SmtpReplayCommand("RSET\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("smtp.recipient_rejected"));
            Assert.That(outcome.Error.Message, Does.Not.Contain(SecretServerMarker));
            Assert.That(outcome.Error.Details?.GetValueOrDefault("protocol"), Is.EqualTo("smtp"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task SenderRejectionReturnsFailedWithSanitizedError()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", $"500 {SecretServerMarker} invalid sender\r\n"),
            new SmtpReplayCommand("RSET\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("smtp.sender_rejected"));
            Assert.That(outcome.Error.Message, Does.Not.Contain(SecretServerMarker));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task MessageRejectionAfterDataReturnsFailedWithSanitizedError()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("DATA\r\n", "354 go ahead\r\n"),
            new SmtpReplayCommand(".\r\n", $"550 {SecretServerMarker} content refused\r\n"),
            new SmtpReplayCommand("RSET\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("smtp.message_rejected"));
            Assert.That(outcome.Error.Message, Does.Not.Contain(SecretServerMarker));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task AdvertisedSizeLimitBelowMessageSizeFailsBeforeMailFrom()
    {
        using var session = new ReplaySession(Conversation(
            capabilities: "250-SIZE 10\r\n",
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("smtp.size_exceeded"));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task ServerSizeRejectionOnMailFromMapsToFailed()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", $"552 {SecretServerMarker} message too large\r\n"),
            new SmtpReplayCommand("RSET\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("smtp.sender_rejected"));
            Assert.That(outcome.Error.Message, Does.Not.Contain(SecretServerMarker));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task DisconnectDuringDataReturnsIndeterminateWithoutResend()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("DATA\r\n", "354 go ahead\r\n"),
            new SmtpReplayCommand(".\r\n", "", SmtpReplayState.UnexpectedDisconnect)));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Indeterminate));
            Assert.That(outcome.Error, Is.Not.Null);
            Assert.That(session.SingleStream.DataCommandCount, Is.EqualTo(1),
                "An ambiguous transport failure must never trigger a resend.");
        });
        session.AssertComplete();
    }

    [Test]
    public async Task TimeoutDuringFinalDataResponseReturnsIndeterminate()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("DATA\r\n", "354 go ahead\r\n"),
            new SmtpReplayCommand(".\r\n", "250 accepted\r\n") { NeverRespond = true }));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session, commandTimeout: TimeSpan.FromMilliseconds(200));

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.That(outcome.State, Is.EqualTo(SendState.Indeterminate));
        session.AssertComplete();
    }

    [Test]
    public async Task DisconnectAfterAcceptedDataStillSucceeds()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("DATA\r\n", "354 go ahead\r\n"),
            new SmtpReplayCommand(".\r\n", "250 accepted\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "", SmtpReplayState.UnexpectedDisconnect)));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.That(outcome.State, Is.EqualTo(SendState.Succeeded));
        session.AssertComplete();
    }

    [Test]
    public async Task InternationalRecipientWithoutSmtpUtf8FailsBeforeMailFrom()
    {
        using var session = new ReplaySession(Conversation(
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential,
            Message("älice@exämple.test"),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("smtp.smtputf8_required"));
            Assert.That(outcome.Error.Category, Is.EqualTo(ErrorCategory.Capability));
        });
        session.AssertComplete();
    }

    [Test]
    public async Task AdvertisedSmtpUtf8IsNotUsedForAsciiMessages()
    {
        using var session = new ReplaySession(Conversation(
            capabilities: "250-SMTPUTF8\r\n",
            new SmtpReplayCommand("MAIL FROM:<user@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("RCPT TO:<alice@example.test>\r\n", "250 ok\r\n"),
            new SmtpReplayCommand("DATA\r\n", "354 go ahead\r\n"),
            new SmtpReplayCommand(".\r\n", "250 accepted\r\n"),
            new SmtpReplayCommand("QUIT\r\n", "221 bye\r\n")));
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.That(outcome.State, Is.EqualTo(SendState.Succeeded));
        session.AssertComplete();
    }

    [Test]
    public async Task AuthenticationFailureBeforeSendReturnsFailedWithMappedError()
    {
        using var session = new ReplaySession(AuthFailureConversation());
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = CreateGateway(session);

        SendTransportOutcome outcome = await gateway.SendAsync(
            Profile(), credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("connection.authentication_failed"));
            Assert.That(outcome.Error.Message, Does.Not.Contain(SecretServerMarker));
        });
        session.AssertComplete();
    }

    [Test]
    public void FactoryRequiresConfiguredSmtpEndpoint()
    {
        AccountProfile profile = Profile() with { Smtp = null };
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var factory = new SmtpClientFactory();

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await factory.CreateAsync(profile, credential, CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("smtp.not_configured"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Capability));
        });
    }

    [TestCase(TlsMode.Plain)]
    [TestCase((TlsMode)999)]
    public async Task GatewayRejectsInsecureSmtpTlsModesBeforeConnecting(TlsMode tlsMode)
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        var gateway = new SmtpGateway(new SmtpClientFactory());
        AccountProfile profile = Profile() with
        {
            Smtp = new EndpointSettings("smtp.example.test", 25, tlsMode)
        };

        SendTransportOutcome outcome = await gateway.SendAsync(
            profile, credential, Message(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.State, Is.EqualTo(SendState.Failed));
            Assert.That(outcome.Error!.Code, Is.EqualTo("connection.tls_required"));
            Assert.That(outcome.Error.Category, Is.EqualTo(ErrorCategory.Validation));
        });
    }

    [Test]
    public void CallerCancellationPropagatesWithoutStableErrorWrapping()
    {
        using PasswordCredentialLease credential = PasswordCredentialLease.FromCharacters("secret");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var gateway = new SmtpGateway(new CanceledFactory());

        Assert.That(async () => await gateway.SendAsync(
                Profile(), credential, Message(), cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user", AuthenticationKind.Password,
        null, null, new EndpointSettings("smtp.example.test", 465, TlsMode.ImplicitTls));

    private static SmtpGateway CreateGateway(
        ReplaySession session, TimeSpan? commandTimeout = null) =>
        new(session, commandTimeout: commandTimeout ?? TimeSpan.FromSeconds(5));

    private static PreparedOutgoingMessage Message(params string[] recipients)
    {
        const string mime =
            "From: user@example.test\r\n" +
            "To: alice@example.test\r\n" +
            "Subject: Replay message\r\n" +
            "Message-Id: <id-1@mailkit-agent.local>\r\n" +
            "\r\n" +
            "hello replay\r\n";
        return new PreparedOutgoingMessage(
            "prep-1",
            "personal",
            "id-1@mailkit-agent.local",
            new string('a', 64),
            Encoding.UTF8.GetBytes(mime),
            "user@example.test",
            recipients.Length > 0 ? recipients : ["alice@example.test"],
            Preview(),
            new string('b', 64),
            Now.AddMinutes(10));
    }

    private static SendPreview Preview() => new(
        "prep-1",
        "personal",
        "id-1@mailkit-agent.local",
        null,
        [],
        [],
        [],
        null,
        null,
        0,
        [],
        Now,
        Now.AddMinutes(10),
        new string('a', 64),
        new string('b', 64),
        "token");

    private static IReadOnlyList<SmtpReplayCommand> Conversation(
        params SmtpReplayCommand[] tail) =>
        BuildConversation(string.Empty, tail);

    private static IReadOnlyList<SmtpReplayCommand> Conversation(
        string capabilities,
        params SmtpReplayCommand[] tail) =>
        BuildConversation(capabilities, tail);

    private static IReadOnlyList<SmtpReplayCommand> BuildConversation(
        string capabilities,
        params SmtpReplayCommand[] tail)
    {
        var commands = new List<SmtpReplayCommand>
        {
            new SmtpReplayCommand("", "220 smtp.example.test ESMTP replay ready\r\n"),
            new SmtpReplayCommand("EHLO replay.local\r\n",
                "250-smtp.example.test\r\n" + capabilities + "250 AUTH LOGIN\r\n"),
            new SmtpReplayCommand("AUTH LOGIN\r\n", $"334 {Base64("user")}\r\n"),
            new($"{Base64("user")}\r\n", $"334 {Base64("secret")}\r\n"),
            new($"{Base64("secret")}\r\n", "235 authenticated\r\n")
        };
        commands.AddRange(tail);
        return commands;
    }

    private static IReadOnlyList<SmtpReplayCommand> AuthFailureConversation() => new[]
    {
        new SmtpReplayCommand("", "220 smtp.example.test ESMTP replay ready\r\n"),
        new SmtpReplayCommand("EHLO replay.local\r\n", "250-smtp.example.test\r\n250 AUTH LOGIN\r\n"),
        new SmtpReplayCommand("AUTH LOGIN\r\n", $"334 {Base64("user")}\r\n"),
        new SmtpReplayCommand($"{Base64("user")}\r\n", $"334 {Base64("secret")}\r\n"),
        new SmtpReplayCommand($"{Base64("secret")}\r\n", $"535 {SecretServerMarker} auth failed\r\n")
    };

    private static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private sealed class ReplaySession : ISmtpClientFactory, IDisposable
    {
        private readonly Queue<IReadOnlyList<SmtpReplayCommand>> conversations;
        private readonly List<SmtpClient> clients = [];
        private readonly List<SmtpReplayStream> streams = [];

        public ReplaySession(params IReadOnlyList<SmtpReplayCommand>[] conversations)
        {
            this.conversations = new Queue<IReadOnlyList<SmtpReplayCommand>>(conversations);
        }

        public SmtpReplayStream SingleStream
        {
            get
            {
                Assert.That(streams, Has.Count.EqualTo(1));
                return streams[0];
            }
        }

        public async Task<SmtpClient> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken)
        {
            Assert.That(conversations, Is.Not.Empty, "Gateway requested an unexpected SMTP session.");
            var stream = new SmtpReplayStream(conversations.Dequeue());
            var client = new SmtpClient { LocalDomain = "replay.local" };
            streams.Add(stream);
            clients.Add(client);

            await client.ConnectAsync(
                stream, profile.Smtp!.Host, profile.Smtp.Port,
                SecureSocketOptions.None, cancellationToken);
            await client.AuthenticateAsync(
                credential.CreateNetworkCredential(profile.Username), cancellationToken);
            return client;
        }

        public void AssertComplete()
        {
            Assert.That(conversations, Is.Empty, "Not every expected SMTP session was opened.");
            Assert.Multiple(() =>
            {
                foreach (SmtpReplayStream stream in streams)
                    stream.AssertComplete();
            });
        }

        public void Dispose()
        {
            foreach (SmtpClient client in clients)
                client.Dispose();
            foreach (SmtpReplayStream stream in streams)
                stream.Dispose();
        }
    }

    private sealed class CanceledFactory : ISmtpClientFactory
    {
        public Task<SmtpClient> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            Task.FromCanceled<SmtpClient>(cancellationToken);
    }
}
