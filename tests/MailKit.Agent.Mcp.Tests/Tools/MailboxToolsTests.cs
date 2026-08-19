using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Paging;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Mail.Attachments;
using MailKit.Agent.Mcp.Tools;

namespace MailKit.Agent.Mcp.Tests.Tools;

public class MailboxToolsTests
{
    private static readonly MessageReference ImapReference =
        MessageReference.ForImap("work", "INBOX", 4242, 100);

    private static readonly MessageReference Pop3Reference =
        MessageReference.ForPop3("work", "fixture-uidl-1");

    [Test]
    public async Task MessageReadDefaultsToMarkAsReadAndForwardsArgumentsExactly()
    {
        var gateway = new RecordingImapGateway();
        var application = CreateMailboxApplication(gateway);

        var result = await ImapTools.ReadAsync(
            new MessageReadRequest(ImapReference),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(gateway.ReadCalls, Has.Count.EqualTo(1));
            Assert.That(gateway.ReadCalls[0].Reference, Is.EqualTo(ImapReference));
            Assert.That(gateway.ReadCalls[0].MarkAsRead, Is.True);
            Assert.That(gateway.ReadCalls[0].BodyMode, Is.EqualTo(BodyMode.SafeText));
            Assert.That(result.Data!.Untrusted, Is.True);
            Assert.That(result.Data.ReadStateSupported, Is.True);
            Assert.That(JsonSerializer.Serialize(result), Does.Contain("\"untrusted\":true"));
        });
    }

    [Test]
    public async Task MessageReadForwardsExplicitUnreadAndHtmlOptions()
    {
        var gateway = new RecordingImapGateway();
        var application = CreateMailboxApplication(gateway);

        var result = await ImapTools.ReadAsync(
            new MessageReadRequest(ImapReference, false, BodyMode.Html),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(gateway.ReadCalls[0].MarkAsRead, Is.False);
            Assert.That(gateway.ReadCalls[0].BodyMode, Is.EqualTo(BodyMode.Html));
        });
    }

    [Test]
    public async Task Pop3ReadAlwaysForwardsMarkAsReadFalseAndReportsReadStateFields()
    {
        var gateway = new RecordingPop3Gateway();
        var application = CreateMailboxApplication(pop3Gateway: gateway);

        var result = await Pop3Tools.ReadAsync(
            new Pop3MessageReadRequest(Pop3Reference),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(gateway.ReadCalls, Has.Count.EqualTo(1));
            Assert.That(gateway.ReadCalls[0], Is.EqualTo(BodyMode.SafeText));
            Assert.That(result.Data!.ReadStateSupported, Is.False);
            Assert.That(result.Data.IsRead, Is.Null);
            Assert.That(result.Data.ReadStateUpdated, Is.False);
            Assert.That(result.Data.Untrusted, Is.True);
        });
    }

    [Test]
    public async Task MessageListForwardsPagingAndSurfacesOpaqueCursorAcrossPages()
    {
        var gateway = new RecordingImapGateway
        {
            Envelopes =
            [
                Envelope(100, "first"),
                Envelope(101, "second")
            ]
        };
        var application = CreateMailboxApplication(gateway);

        var firstPage = await ImapTools.ListAsync(
            new MessageListRequest("work", "INBOX", 1),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.Ok, Is.True);
            Assert.That(gateway.ListCalls[0].Offset, Is.EqualTo(0));
            Assert.That(gateway.ListCalls[0].PageSize, Is.EqualTo(1));
            Assert.That(firstPage.Data!.Messages, Has.Count.EqualTo(1));
            Assert.That(firstPage.Data.NextCursor, Is.Not.Null.And.Not.Empty);
            Assert.That(firstPage.Data.NextCursor, Does.Not.Contain("work"));
        });

        var secondPage = await ImapTools.ListAsync(
            new MessageListRequest("work", "INBOX", 1, firstPage.Data!.NextCursor),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(secondPage.Ok, Is.True);
            Assert.That(gateway.ListCalls[1].Offset, Is.EqualTo(1));
            Assert.That(secondPage.Data!.Messages[0].Subject, Is.EqualTo("second"));
            Assert.That(secondPage.Data.NextCursor, Is.Null);
        });
    }

    [Test]
    public async Task MessageMarkReadForwardsReferencesAndTargetFlag()
    {
        var gateway = new RecordingImapGateway();
        var application = CreateMailboxApplication(gateway);

        var result = await ImapTools.MarkReadAsync(
            new MessageMarkReadRequest([ImapReference], IsRead: false),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(gateway.MarkReadCalls, Has.Count.EqualTo(1));
            Assert.That(gateway.MarkReadCalls[0].References, Is.EqualTo(new[] { ImapReference }));
            Assert.That(gateway.MarkReadCalls[0].IsRead, Is.False);
            Assert.That(result.Data, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AttachmentListForwardsReferenceAndReturnsUntrustedNames()
    {
        var gateway = new RecordingImapGateway();
        var application = CreateAttachmentApplication(imapGateway: gateway);

        var result = await AttachmentTools.ListAsync(
            new AttachmentListRequest(ImapReference),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(gateway.ListAttachmentCalls, Has.Count.EqualTo(1));
            Assert.That(gateway.ListAttachmentCalls[0], Is.EqualTo(ImapReference));
            Assert.That(result.Data![0].Id, Is.EqualTo("att-1"));
        });
    }

    [Test]
    public async Task AttachmentSaveSanitizesPathEscapeErrorsWithoutLeakingRoots()
    {
        var gateway = new RecordingImapGateway();
        using var directory = new TemporaryDirectory();
        var application = CreateAttachmentApplication(
            imapGateway: gateway,
            downloadRoot: directory.Path);

        var result = await AttachmentTools.SaveAsync(
            new AttachmentSaveRequest(ImapReference, "att-1", "..\\..\\private-escape-marker.txt"),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(
                result.Error!.Code,
                Is.AnyOf("attachment.invalid_name", "attachment.path_outside_root"));
            Assert.That(
                result.Error.Category,
                Is.AnyOf(ErrorCategory.Validation, ErrorCategory.Policy));
            var json = JsonSerializer.Serialize(result);
            Assert.That(json, Does.Not.Contain("private-escape-marker"));
            Assert.That(json, Does.Not.Contain(directory.Path));
            Assert.That(
                Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories),
                Is.Empty);
        });
    }

    [Test]
    public async Task AttachmentSaveStoresPayloadInsideIsolatedDownloadRoot()
    {
        var gateway = new RecordingImapGateway();
        using var directory = new TemporaryDirectory();
        var application = CreateAttachmentApplication(
            imapGateway: gateway,
            downloadRoot: directory.Path);

        var result = await AttachmentTools.SaveAsync(
            new AttachmentSaveRequest(ImapReference, "att-1", "stored-copy.txt"),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data!.BytesWritten, Is.EqualTo(RecordingImapGateway.AttachmentText.Length));
            Assert.That(
                Path.GetFullPath(Path.GetDirectoryName(result.Data.Path)!),
                Does.StartWith(Path.GetFullPath(directory.Path)));
            Assert.That(File.ReadAllText(result.Data.Path), Is.EqualTo(RecordingImapGateway.AttachmentText));
        });
    }

    [Test]
    public async Task FolderListMapsPolicyDenialToSanitizedEnvelope()
    {
        const string sensitiveMarker = "private-folder-marker";
        var gateway = new RecordingImapGateway
        {
            Folders = [new FolderDescriptor("INBOX", sensitiveMarker, true, [], null)]
        };
        var store = new ConnectionToolsTests.InMemoryStore();
        store.PutAsync(ConnectionToolsTests.CreateProfile("work"), CancellationToken.None)
            .GetAwaiter().GetResult();
        var application = new MailboxApplication(
            store,
            new ConnectionToolsTests.FakeVault(),
            gateway,
            new RecordingPop3Gateway(),
            new HmacCursorCodec(RandomKey(), TimeProvider.System),
            new OperationPolicy(new PolicyLimits(500, 32)),
            MailSafetyLimits.Default);

        var result = await ImapTools.ListFoldersAsync(
            new FolderListRequest("work"),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo("policy.output_limit_exceeded"));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(sensitiveMarker));
        });
    }

    [Test]
    public void MessageReadPropagatesCancellation()
    {
        var gateway = new RecordingImapGateway
        {
            ReadException = new OperationCanceledException("cancellation-marker")
        };
        var application = CreateMailboxApplication(gateway);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => ImapTools.ReadAsync(
            new MessageReadRequest(ImapReference),
            application,
            cancellation.Token));
    }

    private static MailboxApplication CreateMailboxApplication(
        RecordingImapGateway? imapGateway = null,
        RecordingPop3Gateway? pop3Gateway = null) =>
        CreateMailboxApplicationBuilder(imapGateway, pop3Gateway).Application;

    private static (MailboxApplication Application, AttachmentApplication Attachments)
        CreateMailboxApplicationBuilder(
            RecordingImapGateway? imapGateway = null,
            RecordingPop3Gateway? pop3Gateway = null,
            string? downloadRoot = null)
    {
        var store = new ConnectionToolsTests.InMemoryStore();
        store.PutAsync(ConnectionToolsTests.CreateProfile("work"), CancellationToken.None)
            .GetAwaiter().GetResult();
        var vault = new ConnectionToolsTests.FakeVault();
        var imap = imapGateway ?? new RecordingImapGateway();
        var pop3 = pop3Gateway ?? new RecordingPop3Gateway();
        var mailbox = new MailboxApplication(
            store,
            vault,
            imap,
            pop3,
            new HmacCursorCodec(RandomKey(), TimeProvider.System),
            OperationPolicy.Default,
            MailSafetyLimits.Default);

        string root = downloadRoot ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var attachments = new AttachmentApplication(
            store,
            vault,
            imap,
            pop3,
            new AttachmentService(
                new MailKit.Agent.Core.Storage.MailFileOptions(root, Array.Empty<string>()),
                MailSafetyLimits.Default),
            OperationPolicy.Default);
        return (mailbox, attachments);
    }

    private static AttachmentApplication CreateAttachmentApplication(
        RecordingImapGateway? imapGateway = null,
        string? downloadRoot = null) =>
        CreateMailboxApplicationBuilder(imapGateway, downloadRoot: downloadRoot).Attachments;

    private static byte[] RandomKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    private static MessageEnvelope Envelope(uint uid, string subject) => new(
        MessageReference.ForImap("work", "INBOX", 4242, uid),
        subject,
        ["fixture@example.test"],
        ["to@example.test"],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        128,
        [],
        false);

    private sealed class RecordingImapGateway : IImapGateway
    {
        internal const string AttachmentText = "fixture attachment payload";

        public List<(MessageReference Reference, bool MarkAsRead, BodyMode BodyMode)> ReadCalls { get; } = [];

        public List<(int Offset, int PageSize)> ListCalls { get; } = [];

        public List<(IReadOnlyList<MessageReference> References, bool IsRead)> MarkReadCalls { get; } = [];

        public List<MessageReference> ListAttachmentCalls { get; } = [];

        public IReadOnlyList<FolderDescriptor> Folders { get; init; } =
            [new FolderDescriptor("INBOX", "Inbox", true, [], null)];

        public IReadOnlyList<MessageEnvelope> Envelopes { get; init; } = [];

        public Exception? ReadException { get; init; }

        public Task<IReadOnlyList<FolderDescriptor>> ListFoldersAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            Task.FromResult(Folders);

        public Task<MessagePage> ListMessagesAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            string folderId, int offset, int pageSize, CancellationToken cancellationToken)
        {
            ListCalls.Add((offset, pageSize));
            return Task.FromResult(Page(offset, pageSize));
        }

        public Task<MessagePage> SearchAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            string folderId, MessageSearchCriteria criteria, int offset, int pageSize,
            CancellationToken cancellationToken) =>
            Task.FromResult(Page(offset, pageSize));

        public Task<MessageContent> ReadAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, bool markAsRead, BodyMode bodyMode,
            CancellationToken cancellationToken)
        {
            if (ReadException is not null)
                return Task.FromException<MessageContent>(ReadException);

            ReadCalls.Add((reference, markAsRead, bodyMode));
            return Task.FromResult(new MessageContent
            {
                Text = "fixture body",
                Untrusted = true,
                ReadStateSupported = true,
                IsRead = markAsRead,
                ReadStateUpdated = markAsRead
            });
        }

        public Task<int> MarkReadAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            IReadOnlyList<MessageReference> references, bool isRead,
            CancellationToken cancellationToken)
        {
            MarkReadCalls.Add((references, isRead));
            return Task.FromResult(references.Count);
        }

        public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, CancellationToken cancellationToken)
        {
            ListAttachmentCalls.Add(reference);
            return Task.FromResult<IReadOnlyList<AttachmentDescriptor>>(
                [new AttachmentDescriptor("att-1", "fixture.txt", "text/plain", 24, false, null)]);
        }

        public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, string attachmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new OpenedAttachment(
                new AttachmentDescriptor("att-1", "fixture.txt", "text/plain", 24, false, null),
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(AttachmentText))));

        public async Task<Stream> OpenAttachmentAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, string attachmentId,
            CancellationToken cancellationToken)
        {
            OpenedAttachment opened = await OpenAttachmentWithDescriptorAsync(
                profile, credential, reference, attachmentId, cancellationToken);
            return opened.Content;
        }

        private MessagePage Page(int offset, int pageSize)
        {
            IEnumerable<MessageEnvelope> page = Envelopes.Skip(offset).Take(pageSize);
            int? nextOffset = offset + pageSize < Envelopes.Count ? offset + pageSize : null;
            return new MessagePage(page.ToArray(), nextOffset);
        }
    }

    private sealed class RecordingPop3Gateway : IPop3Gateway
    {
        public List<BodyMode> ReadCalls { get; } = [];

        public Task<MessagePage> ListMessagesAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            int offset, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult(new MessagePage(
            [
                new MessageEnvelope(
                    MessageReference.ForPop3("work", "fixture-uidl-1"),
                    "fixture pop3",
                    ["fixture@example.test"],
                    ["to@example.test"],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    64,
                    [],
                    false)
            ], null));

        public Task<MessageContent> ReadAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, BodyMode bodyMode,
            CancellationToken cancellationToken)
        {
            ReadCalls.Add(bodyMode);
            return Task.FromResult(new MessageContent
            {
                Text = "fixture pop3 body",
                Untrusted = true,
                ReadStateSupported = false,
                IsRead = null,
                ReadStateUpdated = false
            });
        }

        public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttachmentDescriptor>>([]);

        public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, string attachmentId,
            CancellationToken cancellationToken) =>
            throw new MailOperationException(new ToolError(
                "attachment.not_found",
                ErrorCategory.Validation,
                "The requested attachment was not found.",
                false,
                null,
                null));

        public Task<Stream> OpenAttachmentAsync(
            AccountProfile profile, PasswordCredentialLease credential,
            MessageReference reference, string attachmentId,
            CancellationToken cancellationToken) =>
            throw new MailOperationException(new ToolError(
                "attachment.not_found",
                ErrorCategory.Validation,
                "The requested attachment was not found.",
                false,
                null,
                null));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mailkit-agent-mailbox-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
