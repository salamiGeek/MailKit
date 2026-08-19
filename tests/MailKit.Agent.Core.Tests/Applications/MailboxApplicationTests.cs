using System.Security.Cryptography;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Paging;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Tests.Applications;

public class MailboxApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task ListImapReturnsCursorBoundToAccountFolderAndPageSize()
    {
        var gateway = new FakeImapGateway
        {
            Page = new MessagePage([Envelope(MessageReference.ForImap("personal", "INBOX", 7, 9))], 25)
        };
        var fixture = CreateFixture(imap: gateway);

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", "INBOX", 25, cursor: null, CancellationToken.None);

        CursorPayload payload = fixture.CursorCodec.Decode(result.Data!.NextCursor!);
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data.NextOffset, Is.Null, "Raw gateway offsets must not escape the application boundary.");
            Assert.That(payload.AccountId, Is.EqualTo("personal"));
            Assert.That(payload.Scope, Is.EqualTo("imap:list:INBOX"));
            Assert.That(payload.Position, Is.EqualTo("25"));
            Assert.That(payload.PageSize, Is.EqualTo(25));
            Assert.That(gateway.LastOffset, Is.Zero);
        });
    }

    [Test]
    public async Task ListImapRejectsUnknownAccountBeforeCredentialOrGatewayAccess()
    {
        var store = new FakeAccountStore { Profile = null };
        var vault = new FakeVault();
        var gateway = new FakeImapGateway();
        var fixture = CreateFixture(store, vault, gateway);

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "missing", "INBOX", 25, null, CancellationToken.None);

        AssertFailure(result, "account.not_found", ErrorCategory.Validation);
        Assert.Multiple(() =>
        {
            Assert.That(vault.StatusCalls, Is.Zero);
            Assert.That(gateway.ListCalls, Is.Zero);
        });
    }

    [Test]
    public async Task ListImapRejectsMissingEndpointBeforeCredentialAccess()
    {
        var profile = Profile() with { Imap = null };
        var vault = new FakeVault();
        var fixture = CreateFixture(new FakeAccountStore { Profile = profile }, vault);

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", "INBOX", 25, null, CancellationToken.None);

        AssertFailure(result, "imap.not_configured", ErrorCategory.Capability);
        Assert.That(vault.StatusCalls, Is.Zero);
    }

    [Test]
    public async Task ListImapRejectsMissingCredentialBeforeLeaseAcquisition()
    {
        var vault = new FakeVault { Configured = false };
        var fixture = CreateFixture(vault: vault);

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", "INBOX", 25, null, CancellationToken.None);

        AssertFailure(result, "credential.not_configured", ErrorCategory.Authentication);
        Assert.That(vault.PasswordCalls, Is.Zero);
    }

    [TestCase(0)]
    [TestCase(101)]
    public async Task ListImapRejectsPageSizeOutsideSafetyLimit(int pageSize)
    {
        var gateway = new FakeImapGateway();
        var fixture = CreateFixture(imap: gateway);

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", "INBOX", pageSize, null, CancellationToken.None);

        AssertFailure(result, "validation.invalid_page_size", ErrorCategory.Validation);
        Assert.That(gateway.ListCalls, Is.Zero);
    }

    [TestCase("other", "imap:list:INBOX", "0", 25)]
    [TestCase("personal", "imap:list:Archive", "0", 25)]
    [TestCase("personal", "imap:list:INBOX", "-1", 25)]
    [TestCase("personal", "imap:list:INBOX", "0", 10)]
    public async Task ListImapRejectsCursorBindingMismatch(
        string cursorAccount, string cursorScope, string position, int cursorPageSize)
    {
        var fixture = CreateFixture();
        string cursor = fixture.CursorCodec.Encode(new CursorPayload(
            cursorAccount, cursorScope, position, Now.AddMinutes(5)) { PageSize = cursorPageSize });

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", "INBOX", 25, cursor, CancellationToken.None);

        AssertFailure(result, "paging.invalid_cursor", ErrorCategory.Validation);
    }

    [Test]
    public async Task SearchCursorScopeUsesCanonicalCriteriaHash()
    {
        var gateway = new FakeImapGateway
        {
            Page = new MessagePage(Array.Empty<MessageEnvelope>(), 3)
        };
        var fixture = CreateFixture(imap: gateway);
        var criteria = new MessageSearchCriteria
        {
            Subject = "Quarterly report",
            Since = new DateTime(2026, 8, 1),
            Unread = true
        };

        ToolResult<MessagePage> result = await fixture.Application.SearchImapAsync(
            "personal", "INBOX", criteria, 10, null, CancellationToken.None);

        CursorPayload payload = fixture.CursorCodec.Decode(result.Data!.NextCursor!);
        Assert.That(payload.Scope, Does.Match("^imap:search:INBOX:[0-9a-f]{64}$"));
    }

    [Test]
    public async Task SearchRejectsNullCriteriaWithStableValidationFailure()
    {
        var fixture = CreateFixture();

        ToolResult<MessagePage> result = await fixture.Application.SearchImapAsync(
            "personal", "INBOX", null!, 10, null, CancellationToken.None);

        AssertFailure(result, "validation.invalid_search_criteria", ErrorCategory.Validation);
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ImapListRejectsBlankFolderWithStableValidationFailure(string folderId)
    {
        var fixture = CreateFixture();

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", folderId, 10, null, CancellationToken.None);

        AssertFailure(result, "validation.invalid_folder_id", ErrorCategory.Validation);
    }

    [Test]
    public async Task SearchRejectsBlankFolderWithStableValidationFailure()
    {
        var fixture = CreateFixture();

        ToolResult<MessagePage> result = await fixture.Application.SearchImapAsync(
            "personal", " ", new MessageSearchCriteria(), 10, null, CancellationToken.None);

        AssertFailure(result, "validation.invalid_folder_id", ErrorCategory.Validation);
    }

    [Test]
    public async Task ReadPropagatesUidValidityConflictAsStableFailure()
    {
        var conflict = new ToolError(
            "message.reference_conflict", ErrorCategory.Conflict,
            "The message reference no longer identifies the same IMAP folder state.",
            false, null, null);
        var gateway = new FakeImapGateway { ReadException = new MailOperationException(conflict) };
        var fixture = CreateFixture(imap: gateway);

        ToolResult<MessageContent> result = await fixture.Application.ReadAsync(
            MessageReference.ForImap("personal", "INBOX", 7, 9), false,
            BodyMode.SafeText, CancellationToken.None);

        AssertFailure(result, "message.reference_conflict", ErrorCategory.Conflict);
    }

    [Test]
    public async Task ReadRejectsActualSerializedBodyOverOutputPolicyLimit()
    {
        var gateway = new FakeImapGateway
        {
            Content = new MessageContent
            {
                Text = new string('x', 500),
                OriginalCharacterCount = 500,
                ReturnedCharacterCount = 500
            }
        };
        var policy = new OperationPolicy(new PolicyLimits(500, 200));
        var fixture = CreateFixture(imap: gateway, policy: policy);

        ToolResult<MessageContent> result = await fixture.Application.ReadAsync(
            MessageReference.ForImap("personal", "INBOX", 7, 9), false,
            BodyMode.SafeText, CancellationToken.None);

        AssertFailure(result, "policy.output_limit_exceeded", ErrorCategory.Policy);
    }

    [Test]
    public async Task MarkReadRejectsBatchOverPolicyLimitBeforeGatewayAccess()
    {
        var gateway = new FakeImapGateway();
        var fixture = CreateFixture(
            imap: gateway,
            policy: new OperationPolicy(new PolicyLimits(2, int.MaxValue)));
        MessageReference[] references =
        [
            MessageReference.ForImap("personal", "INBOX", 7, 1),
            MessageReference.ForImap("personal", "INBOX", 7, 2),
            MessageReference.ForImap("personal", "INBOX", 7, 3)
        ];

        ToolResult<int> result = await fixture.Application.MarkReadAsync(
            references, true, CancellationToken.None);

        AssertFailure(result, "policy.batch_limit_exceeded", ErrorCategory.Policy);
        Assert.That(gateway.MarkReadCalls, Is.Zero);
    }

    [Test]
    public async Task MarkReadRejectsNullReferencesWithStableValidationFailure()
    {
        var fixture = CreateFixture();

        ToolResult<int> result = await fixture.Application.MarkReadAsync(
            null!, true, CancellationToken.None);

        AssertFailure(result, "validation.invalid_references", ErrorCategory.Validation);
    }

    [Test]
    public async Task ListImapRejectsActualReturnedMessageCountOverPolicyLimit()
    {
        var gateway = new FakeImapGateway
        {
            Page = new MessagePage(
            [
                Envelope(MessageReference.ForImap("personal", "INBOX", 7, 1)),
                Envelope(MessageReference.ForImap("personal", "INBOX", 7, 2))
            ],
            null)
        };
        var fixture = CreateFixture(
            imap: gateway,
            policy: new OperationPolicy(new PolicyLimits(1, int.MaxValue)));

        ToolResult<MessagePage> result = await fixture.Application.ListImapAsync(
            "personal", "INBOX", 1, null, CancellationToken.None);

        AssertFailure(result, "policy.batch_limit_exceeded", ErrorCategory.Policy);
    }

    [Test]
    public async Task ListFoldersRejectsActualReturnedCountOverPolicyLimit()
    {
        var gateway = new FakeImapGateway
        {
            Folders =
            [
                new FolderDescriptor("INBOX", "INBOX", true, [], "inbox"),
                new FolderDescriptor("Archive", "Archive", true, [], "archive")
            ]
        };
        var fixture = CreateFixture(
            imap: gateway,
            policy: new OperationPolicy(new PolicyLimits(1, int.MaxValue)));

        ToolResult<IReadOnlyList<FolderDescriptor>> result =
            await fixture.Application.ListFoldersAsync("personal", CancellationToken.None);

        AssertFailure(result, "policy.batch_limit_exceeded", ErrorCategory.Policy);
    }

    [Test]
    public async Task Pop3ListUsesBoundCursorAndPop3Gateway()
    {
        var pop3 = new FakePop3Gateway
        {
            Page = new MessagePage([Envelope(MessageReference.ForPop3("personal", "uidl-1"))], 5)
        };
        var fixture = CreateFixture(pop3: pop3);

        ToolResult<MessagePage> result = await fixture.Application.ListPop3Async(
            "personal", 5, null, CancellationToken.None);

        CursorPayload payload = fixture.CursorCodec.Decode(result.Data!.NextCursor!);
        Assert.Multiple(() =>
        {
            Assert.That(payload.Scope, Is.EqualTo("pop3:list"));
            Assert.That(payload.PageSize, Is.EqualTo(5));
            Assert.That(pop3.ListCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void ReadPropagatesCallerCancellationAndDisposesCredentialLease()
    {
        var vault = new FakeVault();
        var gateway = new FakeImapGateway
        {
            ReadException = new OperationCanceledException()
        };
        var fixture = CreateFixture(vault: vault, imap: gateway);

        Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Application.ReadAsync(
            MessageReference.ForImap("personal", "INBOX", 7, 9), false,
            BodyMode.SafeText, CancellationToken.None));
        Assert.That(vault.LastLeaseIsDisposed, Is.True);
    }

    private static Fixture CreateFixture(
        FakeAccountStore? store = null,
        FakeVault? vault = null,
        FakeImapGateway? imap = null,
        FakePop3Gateway? pop3 = null,
        OperationPolicy? policy = null)
    {
        var codec = new HmacCursorCodec(
            SHA256.HashData("mailbox-application-test-key"u8.ToArray()),
            new FakeTimeProvider(Now));
        return new Fixture(
            new MailboxApplication(
                store ?? new FakeAccountStore { Profile = Profile() },
                vault ?? new FakeVault(),
                imap ?? new FakeImapGateway(),
                pop3 ?? new FakePop3Gateway(),
                codec,
                policy ?? OperationPolicy.Default,
                MailSafetyLimits.Default,
                new FakeTimeProvider(Now)),
            codec);
    }

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.com", AuthenticationKind.Password,
        new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
        new EndpointSettings("pop.example.com", 995, TlsMode.ImplicitTls),
        new EndpointSettings("smtp.example.com", 465, TlsMode.ImplicitTls));

    private static MessageEnvelope Envelope(MessageReference reference) => new(
        reference, "subject", Array.Empty<string>(), Array.Empty<string>(), null, null,
        10, Array.Empty<string>(), false);

    private static void AssertFailure<T>(ToolResult<T> result, string code, ErrorCategory category)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo(code));
            Assert.That(result.Error.Category, Is.EqualTo(category));
        });
    }

    private sealed record Fixture(MailboxApplication Application, HmacCursorCodec CursorCodec);

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAccountStore : IAccountProfileStore
    {
        public AccountProfile? Profile { get; init; }
        public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Profile is not null && Profile.Id == id ? Profile : null);
        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AccountProfile>>(Profile is null ? [] : [Profile]);
        public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeVault : IAccountCredentialVault
    {
        private PasswordCredentialLease? lastLease;
        public bool Configured { get; init; } = true;
        public int StatusCalls { get; private set; }
        public int PasswordCalls { get; private set; }
        public bool LastLeaseIsDisposed
        {
            get
            {
                if (lastLease is null)
                    return false;
                try
                {
                    lastLease.CreateNetworkCredential("probe");
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }
        public ValueTask<CredentialStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken)
        {
            StatusCalls++;
            return ValueTask.FromResult(new CredentialStatus(Configured, Configured ? CredentialKind.Password : null));
        }
        public ValueTask<PasswordCredentialLease> GetPasswordAsync(string accountId, CancellationToken cancellationToken)
        {
            PasswordCalls++;
            lastLease = PasswordCredentialLease.FromCharacters("private-password");
            return ValueTask.FromResult(lastLease);
        }
        public ValueTask SetPasswordAsync(string accountId, string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> DeletePasswordAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class FakeImapGateway : IImapGateway
    {
        public IReadOnlyList<FolderDescriptor> Folders { get; init; } = [];
        public MessagePage Page { get; init; } = new(Array.Empty<MessageEnvelope>(), null);
        public MessageContent Content { get; init; } = new();
        public Exception? ReadException { get; init; }
        public int ListCalls { get; private set; }
        public int MarkReadCalls { get; private set; }
        public int LastOffset { get; private set; }
        public Task<IReadOnlyList<FolderDescriptor>> ListFoldersAsync(AccountProfile profile, PasswordCredentialLease credential, CancellationToken cancellationToken) => Task.FromResult(Folders);
        public Task<MessagePage> ListMessagesAsync(AccountProfile profile, PasswordCredentialLease credential, string folderId, int offset, int pageSize, CancellationToken cancellationToken)
        {
            ListCalls++;
            LastOffset = offset;
            return Task.FromResult(Page);
        }
        public Task<MessagePage> SearchAsync(AccountProfile profile, PasswordCredentialLease credential, string folderId, MessageSearchCriteria criteria, int offset, int pageSize, CancellationToken cancellationToken) => Task.FromResult(Page);
        public Task<MessageContent> ReadAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, bool markAsRead, BodyMode bodyMode, CancellationToken cancellationToken) =>
            ReadException is null ? Task.FromResult(Content) : Task.FromException<MessageContent>(ReadException);
        public Task<int> MarkReadAsync(AccountProfile profile, PasswordCredentialLease credential, IReadOnlyList<MessageReference> references, bool isRead, CancellationToken cancellationToken)
        {
            MarkReadCalls++;
            return Task.FromResult(references.Count);
        }
        public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AttachmentDescriptor>>([]);
        public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken) => Task.FromResult(new OpenedAttachment(new AttachmentDescriptor(attachmentId, null, "application/octet-stream", 0, false, null), new MemoryStream()));
        public Task<Stream> OpenAttachmentAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakePop3Gateway : IPop3Gateway
    {
        public MessagePage Page { get; init; } = new(Array.Empty<MessageEnvelope>(), null);
        public int ListCalls { get; private set; }
        public Task<MessagePage> ListMessagesAsync(AccountProfile profile, PasswordCredentialLease credential, int offset, int pageSize, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(Page);
        }
        public Task<MessageContent> ReadAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, BodyMode bodyMode, CancellationToken cancellationToken) => Task.FromResult(new MessageContent());
        public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AttachmentDescriptor>>([]);
        public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken) => Task.FromResult(new OpenedAttachment(new AttachmentDescriptor(attachmentId, null, "application/octet-stream", 0, false, null), new MemoryStream()));
        public Task<Stream> OpenAttachmentAsync(AccountProfile profile, PasswordCredentialLease credential, MessageReference reference, string attachmentId, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());
    }
}
