using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MailKit.Agent.Mcp.Testing;

/// <summary>
/// Debug-only registration of deterministic fake mail gateways for stdio process
/// tests. The registration body is compiled only in DEBUG builds and activates only
/// when <c>MAILKIT_AGENT_TEST_MODE=1</c> plus non-secret <c>--test-fixture:*</c>
/// switches are supplied. Production Release builds reject the environment variable
/// before any fake can be registered.
/// </summary>
public static class TestGatewayRegistration
{
    public const string TestModeEnvironmentVariable = "MAILKIT_AGENT_TEST_MODE";
    public const string FixtureSwitchPrefix = "--test-fixture:";

    public static bool IsTestModeRequested() =>
        string.Equals(
            Environment.GetEnvironmentVariable(TestModeEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static IReadOnlySet<string> ParseRequestedFixtures(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var fixtures = new HashSet<string>(StringComparer.Ordinal);
        foreach (string argument in arguments)
        {
            if (!argument.StartsWith(FixtureSwitchPrefix, StringComparison.Ordinal))
                continue;

            foreach (string fixture in argument[FixtureSwitchPrefix.Length..]
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                fixtures.Add(fixture.ToLowerInvariant());
            }
        }

        return fixtures;
    }

#if DEBUG
    public static void ConfigureServices(IServiceCollection services, IReadOnlySet<string> fixtures)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(fixtures);

        // Registered after the production services so the last registration wins.
        if (fixtures.Contains("credential"))
            services.AddSingleton<IAccountCredentialVault, FakeCredentialVault>();
        if (fixtures.Contains("connection"))
            services.AddSingleton<IProtocolConnectionTester, FakeProtocolConnectionTester>();
        if (fixtures.Contains("imap"))
            services.AddSingleton<IImapGateway, FakeImapGateway>();
        if (fixtures.Contains("pop3"))
            services.AddSingleton<IPop3Gateway, FakePop3Gateway>();
        if (fixtures.Contains("smtp"))
        {
            services.AddSingleton<ISmtpGateway, FakeSmtpGateway>();

            // The stdio e2e prepare->commit flow must stay unattended, so the smtp
            // fixture also swaps the local human-approval gate for the automatic
            // approver. This is reachable ONLY in DEBUG builds with
            // MAILKIT_AGENT_TEST_MODE=1; Release builds reject the env var before
            // any registration runs, keeping the production gate unconditional.
            services.AddSingleton<ISendCommitApprover, AutomaticSendCommitApprover>();
        }
        if (fixtures.Contains("drafts"))
        {
            // Drafts-mode commits skip the local approval dialog by design (nothing
            // is delivered), so no approver swap is needed for the e2e drafts
            // scenario: only the fake draft store replaces the IMAP-backed one.
            services.AddSingleton<IDraftMessageStore, FakeDraftStore>();
        }
    }
#endif
}

#if DEBUG
internal sealed class FakeCredentialVault : IAccountCredentialVault
{
    public ValueTask<CredentialStatus> GetStatusAsync(
        string accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CredentialStatus(Configured: true, CredentialKind.Password));

    public ValueTask<PasswordCredentialLease> GetPasswordAsync(
        string accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(PasswordCredentialLease.FromCharacters("fixture-password"));

    public ValueTask SetPasswordAsync(
        string accountId,
        string username,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The fake credential vault is read-only.");

    public ValueTask<bool> DeletePasswordAsync(
        string accountId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(false);
}

internal sealed class FakeProtocolConnectionTester : IProtocolConnectionTester
{
    public Task<ProtocolConnectionResult> TestAsync(
        string protocol,
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ProtocolConnectionResult(
            protocol, Connected: true, TlsEstablished: true, Authenticated: true,
            new[] { "fixture" }, null));
}

internal sealed class FakeImapGateway : IImapGateway
{
    internal const string FolderId = "INBOX";
    internal const string AttachmentId = "att-1";
    internal const string AttachmentFileName = "fixture.txt";
    internal static readonly string AttachmentText = "fixture attachment payload";
    internal const uint UidValidity = 4242;

    private readonly ConcurrentDictionary<uint, bool> readFlags = new();

    public Task<IReadOnlyList<FolderDescriptor>> ListFoldersAsync(
        AccountProfile profile, PasswordCredentialLease credential, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FolderDescriptor>>(
        [
            new(FolderId, "Inbox", true, Array.Empty<string>(), null)
        ]);

    public Task<MessagePage> ListMessagesAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        string folderId,
        int offset,
        int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromResult(Page(
            offset,
            pageSize,
            Envelope(profile.Id, 100, "Fixture welcome message", hasAttachments: true),
            Envelope(profile.Id, 101, "Fixture second message", hasAttachments: false)));

    public Task<MessagePage> SearchAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        string folderId,
        MessageSearchCriteria criteria,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        IEnumerable<MessageEnvelope> matches = new[]
        {
            Envelope(profile.Id, 100, "Fixture welcome message", hasAttachments: true),
            Envelope(profile.Id, 101, "Fixture second message", hasAttachments: false)
        };
        if (!string.IsNullOrWhiteSpace(criteria.Subject))
        {
            matches = matches.Where(envelope =>
                envelope.Subject?.Contains(criteria.Subject, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (criteria.Unread.HasValue)
        {
            matches = matches.Where(envelope =>
                IsRead(envelope.Reference.Uid!.Value) != criteria.Unread.Value);
        }

        MessageEnvelope[] selected = matches.ToArray();
        return Task.FromResult(Page(offset, pageSize, selected));
    }

    public Task<MessageContent> ReadAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        bool markAsRead,
        BodyMode bodyMode,
        CancellationToken cancellationToken)
    {
        ValidateImapReference(reference);
        if (markAsRead)
            readFlags[reference.Uid!.Value] = true;

        return Task.FromResult(new MessageContent
        {
            Headers =
            [
                new MessageHeader("Subject", reference.Uid == 100 ? "Fixture welcome message" : "Fixture second message"),
                new MessageHeader("From", "fixture@example.test")
            ],
            Text = reference.Uid == 100 ? "Fixture IMAP body." : "Fixture IMAP second body.",
            Truncated = false,
            ReadStateSupported = true,
            IsRead = IsRead(reference.Uid!.Value),
            ReadStateUpdated = markAsRead,
            Untrusted = true,
            Attachments = reference.Uid == 100 ? [AttachmentDescriptor()] : []
        });
    }

    public Task<int> MarkReadAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        IReadOnlyList<MessageReference> references,
        bool isRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);

        int updated = 0;
        foreach (MessageReference reference in references)
        {
            ValidateImapReference(reference);
            readFlags[reference.Uid!.Value] = isRead;
            updated++;
        }

        return Task.FromResult(updated);
    }

    public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        ValidateImapReference(reference);
        return Task.FromResult<IReadOnlyList<AttachmentDescriptor>>(
            reference.Uid == 100 ? [AttachmentDescriptor()] : []);
    }

    public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        ValidateImapReference(reference);
        if (reference.Uid != 100 || attachmentId != AttachmentId)
            throw MissingAttachment();

        return Task.FromResult(new OpenedAttachment(
            AttachmentDescriptor(),
            new MemoryStream(Encoding.UTF8.GetBytes(AttachmentText))));
    }

    public async Task<Stream> OpenAttachmentAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        OpenedAttachment opened = await OpenAttachmentWithDescriptorAsync(
            profile, credential, reference, attachmentId, cancellationToken);
        return opened.Content;
    }

    private bool IsRead(uint uid) => readFlags.GetValueOrDefault(uid);

    private static void ValidateImapReference(MessageReference? reference)
    {
        if (reference is null || reference.Protocol != MailProtocol.Imap ||
            reference.FolderId != FolderId || reference.Uid is not (100 or 101))
        {
            throw MissingMessage();
        }
    }

    private static AttachmentDescriptor AttachmentDescriptor() =>
        new(AttachmentId, AttachmentFileName, "text/plain", AttachmentText.Length, false, null);

    private static MailOperationException MissingMessage() => new(new ToolError(
        "message.not_found",
        ErrorCategory.Validation,
        "The fixture message was not found.",
        false,
        null,
        null));

    private static MailOperationException MissingAttachment() => new(new ToolError(
        "attachment.not_found",
        ErrorCategory.Validation,
        "The requested attachment was not found.",
        false,
        null,
        null));

    private static MessageEnvelope Envelope(
        string accountId, uint uid, string subject, bool hasAttachments) =>
        new(
            MessageReference.ForImap(accountId, FolderId, UidValidity, uid),
            subject,
            ["fixture@example.test"],
            ["fixture-to@example.test"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            1024,
            hasAttachments ? ["\\Seen"] : Array.Empty<string>(),
            hasAttachments);

    private static MessagePage Page(int offset, int pageSize, params MessageEnvelope[] envelopes)
    {
        IEnumerable<MessageEnvelope> page = envelopes.Skip(Math.Max(0, offset)).Take(pageSize);
        int? nextOffset = offset + pageSize < envelopes.Length ? offset + pageSize : null;
        return new MessagePage(page.ToArray(), nextOffset);
    }
}

internal sealed class FakePop3Gateway : IPop3Gateway
{
    internal const string Uidl = "fixture-uidl-1";
    internal const string AttachmentId = "att-1";
    internal const string AttachmentFileName = "fixture-pop3.txt";
    internal static readonly string AttachmentText = "fixture pop3 attachment payload";

    public Task<MessagePage> ListMessagesAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var envelope = new MessageEnvelope(
            MessageReference.ForPop3(profile.Id, Uidl),
            "Fixture POP3 note",
            ["fixture-pop3@example.test"],
            ["fixture-to@example.test"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            512,
            Array.Empty<string>(),
            true);
        IEnumerable<MessageEnvelope> page = new[] { envelope }
            .Skip(Math.Max(0, offset))
            .Take(pageSize);
        return Task.FromResult(new MessagePage(page.ToArray(), null));
    }

    public Task<MessageContent> ReadAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        BodyMode bodyMode,
        CancellationToken cancellationToken)
    {
        ValidatePop3Reference(reference);
        return Task.FromResult(new MessageContent
        {
            Headers = [new MessageHeader("Subject", "Fixture POP3 note")],
            Text = "Fixture POP3 body.",
            Truncated = false,
            ReadStateSupported = false,
            IsRead = null,
            ReadStateUpdated = false,
            Untrusted = true,
            Attachments = [Descriptor()]
        });
    }

    public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        ValidatePop3Reference(reference);
        return Task.FromResult<IReadOnlyList<AttachmentDescriptor>>([Descriptor()]);
    }

    public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        ValidatePop3Reference(reference);
        if (attachmentId != AttachmentId)
            throw MissingAttachment();

        return Task.FromResult(new OpenedAttachment(
            Descriptor(),
            new MemoryStream(Encoding.UTF8.GetBytes(AttachmentText))));
    }

    public async Task<Stream> OpenAttachmentAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        OpenedAttachment opened = await OpenAttachmentWithDescriptorAsync(
            profile, credential, reference, attachmentId, cancellationToken);
        return opened.Content;
    }

    private static void ValidatePop3Reference(MessageReference? reference)
    {
        if (reference is null || reference.Protocol != MailProtocol.Pop3 || reference.Uidl != Uidl)
            throw MissingMessage();
    }

    private static AttachmentDescriptor Descriptor() =>
        new(AttachmentId, AttachmentFileName, "text/plain", AttachmentText.Length, false, null);

    private static MailOperationException MissingMessage() => new(new ToolError(
        "message.not_found",
        ErrorCategory.Validation,
        "The fixture message was not found.",
        false,
        null,
        null));

    private static MailOperationException MissingAttachment() => new(new ToolError(
        "attachment.not_found",
        ErrorCategory.Validation,
        "The requested attachment was not found.",
        false,
        null,
        null));
}

/// <summary>
/// Records every accepted SMTP delivery as one JSON line under
/// <c>&lt;data&gt;/test-fixtures/smtp-deliveries.jsonl</c> so stdio process tests
/// can assert that a repeated commit never produced a second delivery.
/// </summary>
internal sealed class FakeSmtpGateway : ISmtpGateway
{
    private readonly string deliveryLogPath;
    private readonly object gate = new();

    public FakeSmtpGateway()
    {
        deliveryLogPath = Path.Combine(
            AppDataPaths.Resolve(), "test-fixtures", "smtp-deliveries.jsonl");
    }

    public Task<SendTransportOutcome> SendAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        PreparedOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(message);

        var record = new
        {
            delivered_at = DateTimeOffset.UtcNow,
            account_id = message.AccountId,
            message_id = message.MessageId
        };
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(deliveryLogPath)!);
            File.AppendAllText(
                deliveryLogPath,
                JsonSerializer.Serialize(record) + Environment.NewLine);
        }

        return Task.FromResult(SendTransportOutcome.Succeeded());
    }
}

/// <summary>
/// Records every accepted draft save as one JSON line under
/// <c>&lt;data&gt;/test-fixtures/draft-saves.jsonl</c> (including the exact MIME
/// bytes as base64) so stdio process tests can assert that a drafts-mode commit
/// saved the composed message once, never delivered it, and a repeated commit never
/// saved a second copy.
/// </summary>
internal sealed class FakeDraftStore : IDraftMessageStore
{
    private readonly string saveLogPath;
    private readonly object gate = new();

    public FakeDraftStore()
    {
        saveLogPath = Path.Combine(
            AppDataPaths.Resolve(), "test-fixtures", "draft-saves.jsonl");
    }

    public Task<SendTransportOutcome> SaveAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        PreparedOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(message);

        // Snapshot before the application's finally block zeroes the MIME bytes.
        string mimeBase64 = Convert.ToBase64String(message.MimeMessage);
        var record = new
        {
            saved_at = DateTimeOffset.UtcNow,
            account_id = message.AccountId,
            message_id = message.MessageId,
            mime_base64 = mimeBase64
        };
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(saveLogPath)!);
            File.AppendAllText(
                saveLogPath,
                JsonSerializer.Serialize(record) + Environment.NewLine);
        }

        return Task.FromResult(SendTransportOutcome.Succeeded());
    }
}
#endif
