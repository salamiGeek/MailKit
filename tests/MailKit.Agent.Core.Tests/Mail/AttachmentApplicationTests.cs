using System.Text;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Tests.Mail;

public class AttachmentApplicationTests
{
    [Test]
    public async Task ListUsesDescriptorOnlyGatewayOperation()
    {
        var descriptor = Descriptor();
        var imap = new FakeImapGateway { Attachments = [descriptor] };
        var app = CreateApplication(imap: imap);

        ToolResult<IReadOnlyList<AttachmentDescriptor>> result = await app.ListAsync(
            MessageReference.ForImap("personal", "INBOX", 7, 9), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Data, Is.EqualTo(new[] { descriptor }));
            Assert.That(imap.ListAttachmentCalls, Is.EqualTo(1));
            Assert.That(imap.ReadCalls, Is.Zero);
            Assert.That(imap.OpenCalls, Is.Zero);
        });
    }

    [Test]
    public async Task SaveReResolvesReferenceOpensOneStreamAndPassesDescriptorToWriter()
    {
        var descriptor = Descriptor();
        var store = new FakeStore { Profile = Profile() };
        var imap = new FakeImapGateway
        {
            Attachments = [descriptor],
            AttachmentBytes = Encoding.UTF8.GetBytes("attachment body")
        };
        var writer = new FakeWriter();
        var app = CreateApplication(store, imap, writer: writer);
        var reference = MessageReference.ForImap("personal", "INBOX", 7, 9);

        ToolResult<AttachmentSaveResult> result = await app.SaveAsync(
            reference, "part-1", "safe-name.txt", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(store.GetCalls, Is.EqualTo(1));
            Assert.That(imap.ReadCalls, Is.Zero);
            Assert.That(imap.OpenDescribedCalls, Is.EqualTo(1));
            Assert.That(imap.OpenCalls, Is.Zero);
            Assert.That(imap.LastReference, Is.EqualTo(reference));
            Assert.That(writer.Descriptor, Is.EqualTo(descriptor));
            Assert.That(writer.DestinationName, Is.EqualTo("safe-name.txt"));
            Assert.That(writer.Body, Is.EqualTo("attachment body"));
        });
    }

    [Test]
    public async Task SavePop3UsesOnlyPop3Gateway()
    {
        var descriptor = Descriptor();
        var imap = new FakeImapGateway();
        var pop3 = new FakePop3Gateway
        {
            Attachments = [descriptor]
        };
        var app = CreateApplication(imap: imap, pop3: pop3);

        ToolResult<AttachmentSaveResult> result = await app.SaveAsync(
            MessageReference.ForPop3("personal", "uidl-1"), "part-1", null,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(pop3.OpenDescribedCalls, Is.EqualTo(1));
            Assert.That(imap.OpenCalls, Is.Zero);
        });
    }

    [Test]
    public async Task SaveRejectsUnknownAttachmentBeforeOpeningStream()
    {
        var imap = new FakeImapGateway { Attachments = [Descriptor()] };
        var writer = new FakeWriter();
        var app = CreateApplication(imap: imap, writer: writer);

        ToolResult<AttachmentSaveResult> result = await app.SaveAsync(
            MessageReference.ForImap("personal", "INBOX", 7, 9),
            "part-99", null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Error!.Code, Is.EqualTo("attachment.not_found"));
            Assert.That(imap.OpenDescribedCalls, Is.EqualTo(1));
            Assert.That(imap.OpenCalls, Is.Zero);
            Assert.That(writer.Calls, Is.Zero);
        });
    }

    private static AttachmentApplication CreateApplication(
        FakeStore? store = null,
        FakeImapGateway? imap = null,
        FakePop3Gateway? pop3 = null,
        FakeWriter? writer = null) =>
        new(
            store ?? new FakeStore { Profile = Profile() },
            new FakeVault(),
            imap ?? new FakeImapGateway(),
            pop3 ?? new FakePop3Gateway(),
            writer ?? new FakeWriter(),
            OperationPolicy.Default);

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.com", AuthenticationKind.Password,
        new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
        new EndpointSettings("pop.example.com", 995, TlsMode.ImplicitTls), null);

    private static AttachmentDescriptor Descriptor() =>
        new("part-1", "report.txt", "text/plain", 15, false, null);

    private sealed class FakeStore : IAccountProfileStore
    {
        public AccountProfile? Profile { get; init; }
        public int GetCalls { get; private set; }
        public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(Profile);
        }
        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AccountProfile>>([]);
        public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeVault : IAccountCredentialVault
    {
        public ValueTask<CredentialStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(new CredentialStatus(true, CredentialKind.Password));
        public ValueTask<PasswordCredentialLease> GetPasswordAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(PasswordCredentialLease.FromCharacters("private-password"));
        public ValueTask SetPasswordAsync(string accountId, string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> DeletePasswordAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class FakeImapGateway : IImapGateway
    {
        public MessageContent Content { get; init; } = new();
        public IReadOnlyList<AttachmentDescriptor> Attachments { get; init; } = [];
        public byte[] AttachmentBytes { get; init; } = [1];
        public int ReadCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int ListAttachmentCalls { get; private set; }
        public int OpenDescribedCalls { get; private set; }
        public bool LastMarkAsRead { get; private set; }
        public MessageReference? LastReference { get; private set; }
        public Task<IReadOnlyList<FolderDescriptor>> ListFoldersAsync(AccountProfile profile, PasswordCredentialLease credential, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FolderDescriptor>>([]);
        public Task<MessagePage> ListMessagesAsync(AccountProfile profile, PasswordCredentialLease credential, string folderId, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new MessagePage([], null));
        public Task<MessagePage> SearchAsync(AccountProfile profile, PasswordCredentialLease credential, string folderId, MessageSearchCriteria criteria, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new MessagePage([], null));
        public Task<MessageContent> ReadAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, bool markAsRead, BodyMode bodyMode, CancellationToken cancellationToken)
        {
            ReadCalls++;
            LastMarkAsRead = markAsRead;
            LastReference = reference;
            return Task.FromResult(Content);
        }
        public Task<int> MarkReadAsync(AccountProfile profile, PasswordCredentialLease credential, IReadOnlyList<MessageReference> references, bool isRead, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, CancellationToken cancellationToken)
        {
            ListAttachmentCalls++;
            LastReference = reference;
            return Task.FromResult(Attachments);
        }
        public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken)
        {
            OpenDescribedCalls++;
            LastReference = reference;
            AttachmentDescriptor? descriptor = Attachments.FirstOrDefault(item => item.Id == attachmentId);
            return descriptor is null
                ? Task.FromException<OpenedAttachment>(new MailOperationException(new ToolError(
                    "attachment.not_found", ErrorCategory.Validation, "Not found.", false, null, null)))
                : Task.FromResult(new OpenedAttachment(
                    descriptor, new MemoryStream(AttachmentBytes, writable: false)));
        }
        public Task<Stream> OpenAttachmentAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken)
        {
            OpenCalls++;
            LastReference = reference;
            return Task.FromResult<Stream>(new MemoryStream(AttachmentBytes, writable: false));
        }
    }

    private sealed class FakePop3Gateway : IPop3Gateway
    {
        public MessageContent Content { get; init; } = new();
        public IReadOnlyList<AttachmentDescriptor> Attachments { get; init; } = [];
        public int OpenCalls { get; private set; }
        public int OpenDescribedCalls { get; private set; }
        public Task<MessagePage> ListMessagesAsync(AccountProfile profile, PasswordCredentialLease credential, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult(new MessagePage([], null));
        public Task<MessageContent> ReadAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, BodyMode bodyMode, CancellationToken cancellationToken) => Task.FromResult(Content);
        public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, CancellationToken cancellationToken) => Task.FromResult(Attachments);
        public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken)
        {
            OpenDescribedCalls++;
            AttachmentDescriptor? descriptor = Attachments.FirstOrDefault(item => item.Id == attachmentId);
            return descriptor is null
                ? Task.FromException<OpenedAttachment>(new MailOperationException(new ToolError(
                    "attachment.not_found", ErrorCategory.Validation, "Not found.", false, null, null)))
                : Task.FromResult(new OpenedAttachment(descriptor, new MemoryStream([1])));
        }
        public Task<Stream> OpenAttachmentAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken)
        {
            OpenCalls++;
            return Task.FromResult<Stream>(new MemoryStream([1]));
        }
    }

    private sealed class FakeWriter : IAttachmentWriter
    {
        public int Calls { get; private set; }
        public AttachmentDescriptor? Descriptor { get; private set; }
        public string? DestinationName { get; private set; }
        public string? Body { get; private set; }
        public async Task<AttachmentSaveResult> SaveAsync(Stream source, AttachmentDescriptor descriptor, string? destinationName, CancellationToken cancellationToken)
        {
            Calls++;
            Descriptor = descriptor;
            DestinationName = destinationName;
            using var reader = new StreamReader(source, Encoding.UTF8, leaveOpen: true);
            Body = await reader.ReadToEndAsync(cancellationToken);
            return new AttachmentSaveResult(descriptor.Id, destinationName ?? descriptor.FileName!, "C:/downloads/file", Body.Length);
        }
    }
}
