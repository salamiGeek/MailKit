using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Smtp;
using MimeKit;

namespace MailKit.Agent.Mail.Tests.Smtp;

[TestFixture]
public sealed class OutgoingMessageComposerTests
{
    private static readonly Regex BccHeaderPattern =
        new("^Bcc:", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private string testDirectory = null!;
    private string uploadRoot = null!;
    private OutgoingMessageComposer composer = null!;

    [SetUp]
    public void SetUp()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "MailKit.Agent.Tests", Guid.NewGuid().ToString("N"));
        uploadRoot = Path.Combine(testDirectory, "uploads");
        Directory.CreateDirectory(uploadRoot);
        composer = CreateComposer();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, recursive: true);
    }

    [Test]
    public async Task TextOnlyDraftProducesPlainTextBodyAndDeterministicMessageId()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-001", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        Assert.Multiple(() =>
        {
            Assert.That(composed.MessageId, Is.EqualTo(ExpectedMessageId("personal", "idem-001")));
            Assert.That(parsed.MessageId, Is.EqualTo(composed.MessageId));
            Assert.That(parsed.TextBody, Is.EqualTo("Numbers attached.\r\n"));
            Assert.That(parsed.HtmlBody, Is.Null);
            Assert.That(parsed.Body, Is.InstanceOf<TextPart>());
            Assert.That(((TextPart)parsed.Body!).ContentType.MimeType, Is.EqualTo("text/plain"));
            Assert.That(((TextPart)parsed.Body!).ContentType.Charset, Is.EqualTo("utf-8"));
            Assert.That(parsed.Subject, Is.EqualTo("Quarterly report"));
        });
    }

    [Test]
    public async Task HtmlOnlyDraftProducesHtmlBody()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(htmlBody: "<p>Numbers attached.</p>"), "idem-002", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.HtmlBody, Is.EqualTo("<p>Numbers attached.</p>\r\n"));
            Assert.That(parsed.TextBody, Is.Null);
            Assert.That(((TextPart)parsed.Body!).ContentType.MimeType, Is.EqualTo("text/html"));
        });
    }

    [Test]
    public async Task TextAndHtmlDraftsProduceMultipartAlternative()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(),
            Draft(textBody: "Numbers attached.", htmlBody: "<p>Numbers attached.</p>"),
            "idem-003", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        Multipart body = (Multipart)parsed.Body!;
        Assert.Multiple(() =>
        {
            Assert.That(body.ContentType.MimeType, Is.EqualTo("multipart/alternative"));
            Assert.That(body, Has.Count.EqualTo(2));
            Assert.That(((TextPart)body[0]).ContentType.MimeType, Is.EqualTo("text/plain"));
            Assert.That(((TextPart)body[1]).ContentType.MimeType, Is.EqualTo("text/html"));
        });
    }

    [TestCase("notes.txt", "text/plain")]
    [TestCase("report.pdf", "application/pdf")]
    [TestCase("picture.png", "image/png")]
    public async Task AttachmentsBuildMultipartMixedWithDetectedMimeTypes(
        string fileName, string expectedMimeType)
    {
        string attachmentPath = await CreateAttachmentAsync(fileName, "attachment payload");

        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(textBody: "See attachment.") with { AttachmentPaths = [attachmentPath] },
            "idem-004", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        Multipart body = (Multipart)parsed.Body!;
        MimePart attachment = (MimePart)body[1];
        using MemoryStream content = new();
        await attachment.Content!.DecodeToAsync(content);
        Assert.Multiple(() =>
        {
            Assert.That(body.ContentType.MimeType, Is.EqualTo("multipart/mixed"));
            Assert.That(((TextPart)body[0]).ContentType.MimeType, Is.EqualTo("text/plain"));
            Assert.That(attachment.ContentType.MimeType, Is.EqualTo(expectedMimeType));
            Assert.That(attachment.FileName, Is.EqualTo(fileName));
            Assert.That(attachment.ContentDisposition?.Disposition, Is.EqualTo("attachment"));
            Assert.That(Encoding.UTF8.GetString(content.ToArray()), Is.EqualTo("attachment payload"));
        });
    }

    [Test]
    public async Task BccRecipientsNeverAppearInSerializedMime()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-005", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        string raw = Encoding.UTF8.GetString(composed.MimeMessage);
        Assert.Multiple(() =>
        {
            // The SMTP transport must receive Bcc envelope recipients while the
            // serialized message written to DATA excludes the Bcc header.
            Assert.That(parsed.Bcc, Is.Empty);
            Assert.That(BccHeaderPattern.IsMatch(raw), Is.False,
                "The serialized MIME must not contain a Bcc header.");
            Assert.That(parsed.To.Mailboxes.Select(mailbox => mailbox.Address),
                Is.EqualTo(new[] { "alice@example.test" }));
            Assert.That(parsed.Cc.Mailboxes.Select(mailbox => mailbox.Address),
                Is.EqualTo(new[] { "carol@example.test" }));
        });
    }

    [Test]
    public async Task BodiesAreCrlfNormalizedBeforeWriting()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(),
            Draft(textBody: "first\r\nsecond\rthird\nfourth", htmlBody: "<p>a\nb</p>"),
            "idem-006", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        Assert.Multiple(() =>
        {
            Assert.That(parsed.TextBody, Is.EqualTo("first\r\nsecond\r\nthird\r\nfourth"));
            Assert.That(parsed.HtmlBody, Is.EqualTo("<p>a\r\nb</p>"));
        });
    }

    [Test]
    public async Task IdenticalDraftKeyAndClockReadingReproduceIdenticalBytes()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 19, 4, 0, 0, TimeSpan.Zero));
        OutgoingMessageComposer first = CreateComposer(timeProvider: clock);
        OutgoingMessageComposer second = CreateComposer(timeProvider: clock);

        ComposedOutgoingMessage a = await first.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-007", CancellationToken.None);
        ComposedOutgoingMessage b = await second.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-007", CancellationToken.None);
        ComposedOutgoingMessage c = await second.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-008", CancellationToken.None);

        MimeMessage parsed = Parse(a);
        Assert.Multiple(() =>
        {
            Assert.That(b.MimeMessage, Is.EqualTo(a.MimeMessage),
                "The same account, draft, key, and clock reading must reproduce identical bytes.");
            Assert.That(b.MessageId, Is.EqualTo(a.MessageId));
            Assert.That(c.MessageId, Is.Not.EqualTo(a.MessageId));
            Assert.That(c.MimeMessage, Is.Not.EqualTo(a.MimeMessage));
            Assert.That(parsed.Date, Is.EqualTo(clock.UtcNow),
                "The Date header must come from the injected clock, not the wall clock.");
        });
    }

    [Test]
    public async Task MessageIdIsStableAcrossDifferentClockReadings()
    {
        // Byte identity is clock-scoped (the Date header changes), but message
        // identity — the property redelivery protection relies on — is not.
        var earlier = new MutableTimeProvider(new DateTimeOffset(2026, 8, 19, 4, 0, 0, TimeSpan.Zero));
        var later = new MutableTimeProvider(new DateTimeOffset(2026, 8, 19, 4, 0, 5, TimeSpan.Zero));

        ComposedOutgoingMessage a = await CreateComposer(timeProvider: earlier).ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-007", CancellationToken.None);
        ComposedOutgoingMessage b = await CreateComposer(timeProvider: later).ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-007", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(b.MessageId, Is.EqualTo(a.MessageId),
                "The Message-Id must depend only on the account and idempotency key.");
            Assert.That(Parse(b).Date, Is.Not.EqualTo(Parse(a).Date));
            Assert.That(b.MimeMessage, Is.Not.EqualTo(a.MimeMessage),
                "Different clock readings legitimately produce different Date headers.");
        });
    }

    [Test]
    public async Task MessageIdIsLowercaseBase64UrlSha256OfAccountAndKey()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-key.9", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(composed.MessageId, Is.EqualTo(ExpectedMessageId("personal", "idem-key.9")));
            Assert.That(composed.MessageId, Does.Match("^[a-z0-9_-]+@mailkit-agent\\.local$"));
            Assert.That(composed.MessageId.Any(char.IsUpper), Is.False);
        });
    }

    [Test]
    public async Task MissingFromFallsBackToProfileUsername()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached.") with { From = null },
            "idem-009", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        Assert.That(parsed.From.Mailboxes.Single().Address, Is.EqualTo("user@example.test"));
    }

    [Test]
    public async Task ExplicitFromOverridesProfileUsername()
    {
        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), Draft(textBody: "Numbers attached."), "idem-010", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        MailboxAddress sender = parsed.From.Mailboxes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(sender.Address, Is.EqualTo("bob@example.test"));
            Assert.That(sender.Name, Is.EqualTo("Bob"));
        });
    }

    [Test]
    public async Task UnicodeAddressesArePreservedForSmtpUtf8Transport()
    {
        OutgoingMessageDraft draft = Draft(textBody: "Numbers attached.") with
        {
            To = [new OutgoingMailbox("Älice", "älice@exämple.test")]
        };

        ComposedOutgoingMessage composed = await composer.ComposeAsync(
            Profile(), draft, "idem-011", CancellationToken.None);

        MimeMessage parsed = Parse(composed);
        MailboxAddress recipient = parsed.To.Mailboxes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(recipient.Address, Is.EqualTo("älice@exämple.test"));
            Assert.That(recipient.IsInternational, Is.True);
        });
    }

    [TestCase("not-an-address")]
    [TestCase("missing-at.example.test")]
    public void InvalidRecipientAddressIsRejected(string address)
    {
        OutgoingMessageDraft draft = Draft(textBody: "Numbers attached.") with
        {
            To = [new OutgoingMailbox(null, address)]
        };

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await composer.ComposeAsync(Profile(), draft, "idem-012", CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("validation.invalid_recipient"));
    }

    [Test]
    public void InvalidSenderAddressIsRejected()
    {
        OutgoingMessageDraft draft = Draft(textBody: "Numbers attached.") with
        {
            From = new OutgoingMailbox(null, "not-an-address")
        };

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await composer.ComposeAsync(Profile(), draft, "idem-013", CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("validation.invalid_recipient"));
    }

    [Test]
    public void MissingRecipientsAreRejected()
    {
        OutgoingMessageDraft draft = Draft(textBody: "Numbers attached.") with
        {
            To = [],
            Cc = [],
            Bcc = []
        };

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await composer.ComposeAsync(Profile(), draft, "idem-014", CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("validation.missing_recipients"));
    }

    [Test]
    public void MissingBodyIsRejected()
    {
        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await composer.ComposeAsync(
                Profile(), Draft(textBody: null, htmlBody: null), "idem-015", CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("validation.missing_body"));
            Assert.That(exception.Error.Category, Is.EqualTo(ErrorCategory.Validation));
        });
    }

    [Test]
    public async Task OversizeMessageIsRejected()
    {
        string attachmentPath = await CreateAttachmentAsync("large.bin", new string('x', 2048));
        OutgoingMessageComposer limited = CreateComposer(maxMessageBytes: 1024);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await limited.ComposeAsync(
                Profile(),
                Draft(textBody: "Numbers attached.") with { AttachmentPaths = [attachmentPath] },
                "idem-016", CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("validation.message_too_large"));
    }

    [Test]
    public async Task AttachmentOutsideAllowedRootsIsRejected()
    {
        string outsideDirectory = Path.Combine(testDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        string outsidePath = Path.Combine(outsideDirectory, "secret.txt");
        await File.WriteAllTextAsync(outsidePath, "outside");

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await composer.ComposeAsync(
                Profile(),
                Draft(textBody: "Numbers attached.") with { AttachmentPaths = [outsidePath] },
                "idem-017", CancellationToken.None))!;

        Assert.Multiple(() =>
        {
            Assert.That(exception.Error.Code, Is.EqualTo("attachment.upload_path_not_allowed"));
            Assert.That(exception.Error.Message, Does.Not.Contain(outsidePath));
        });
    }

    [Test]
    public async Task AttachmentReachedThroughSymlinkOutsideRootsIsRejected()
    {
        string outsideDirectory = Path.Combine(testDirectory, "outside");
        Directory.CreateDirectory(outsideDirectory);
        string target = Path.Combine(outsideDirectory, "secret.txt");
        await File.WriteAllTextAsync(target, "outside");
        string link = Path.Combine(uploadRoot, "link.txt");
        File.CreateSymbolicLink(link, target);

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await composer.ComposeAsync(
                Profile(),
                Draft(textBody: "Numbers attached.") with { AttachmentPaths = [link] },
                "idem-018", CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("attachment.upload_path_not_allowed"));
    }

    [Test]
    public void AttachmentWithoutConfiguredUploadRootsIsRejected()
    {
        OutgoingMessageComposer rootless = new(new MailFileOptions(
            Path.Combine(testDirectory, "downloads"), Array.Empty<string>()));

        MailOperationException exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await rootless.ComposeAsync(
                Profile(),
                Draft(textBody: "Numbers attached.") with
                {
                    AttachmentPaths = [Path.Combine(uploadRoot, "any.txt")]
                },
                "idem-019", CancellationToken.None))!;

        Assert.That(exception.Error.Code, Is.EqualTo("attachment.upload_roots_required"));
    }

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.test", AuthenticationKind.Password,
        null, null, new EndpointSettings("smtp.example.test", 465, TlsMode.ImplicitTls));

    private static OutgoingMessageDraft Draft(string? textBody = null, string? htmlBody = null) => new(
        [new OutgoingMailbox("Alice", "alice@example.test")],
        [new OutgoingMailbox(null, "carol@example.test")],
        [new OutgoingMailbox(null, "hidden@example.test")],
        new OutgoingMailbox("Bob", "bob@example.test"),
        "Quarterly report",
        textBody,
        htmlBody,
        null);

    private OutgoingMessageComposer CreateComposer(
        long? maxMessageBytes = null, TimeProvider? timeProvider = null) => new(
        new MailFileOptions(Path.Combine(testDirectory, "downloads"), [uploadRoot]),
        maxMessageBytes,
        timeProvider);

    private static string ExpectedMessageId(string accountId, string idempotencyKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountId + "\0" + idempotencyKey));
        string base64Url = Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')
            .ToLowerInvariant();
        return $"{base64Url}@mailkit-agent.local";
    }

    private static MimeMessage Parse(ComposedOutgoingMessage composed)
    {
        using var stream = new MemoryStream(composed.MimeMessage);
        return MimeMessage.Load(stream, persistent: false);
    }

    private async Task<string> CreateAttachmentAsync(string fileName, string content)
    {
        string path = Path.Combine(uploadRoot, fileName);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow => utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
