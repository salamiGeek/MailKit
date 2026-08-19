using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Mail.Connections;
using MailKit.Agent.Mail.Mime;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MimeKit.Utils;

namespace MailKit.Agent.Mail.Imap;

public sealed class ImapGateway : IImapGateway
{
    private const MessageSummaryItems SummaryItems =
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.Flags |
        MessageSummaryItems.Size |
        MessageSummaryItems.InternalDate |
        MessageSummaryItems.Envelope |
        MessageSummaryItems.BodyStructure;

    private readonly IImapClientFactory clientFactory;
    private readonly MimeContentService contentService;
    private readonly MimePartLocator partLocator;
    private readonly ConnectionGate connectionGate;
    private readonly TimeSpan commandTimeout;
    private readonly int maxBodyCharacters;

    public ImapGateway()
        : this(new ImapClientFactory())
    {
    }

    public ImapGateway(
        IImapClientFactory clientFactory,
        MimeContentService? contentService = null,
        MimePartLocator? partLocator = null,
        TimeSpan? commandTimeout = null,
        int maxBodyCharacters = 100_000,
        ConnectionGate? connectionGate = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.contentService = contentService ?? new MimeContentService();
        this.partLocator = partLocator ?? new MimePartLocator();
        this.connectionGate = connectionGate ?? new ConnectionGate();
        this.commandTimeout = commandTimeout ?? ConnectionLimits.Default.CommandTimeout;
        if (this.commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        if (maxBodyCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBodyCharacters));
        this.maxBodyCharacters = maxBodyCharacters;
    }

    public Task<IReadOnlyList<FolderDescriptor>> ListFoldersAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        CancellationToken cancellationToken) =>
        WithClientAsync(profile, credential, "folder_list", async client =>
        {
            if (client.PersonalNamespaces.Count == 0)
                throw CapabilityError("imap.namespace_unavailable", "The IMAP server did not provide a personal namespace.");

            IList<IMailFolder> folders;
            using (var scope = CreateScope(cancellationToken))
            {
                folders = await client.GetFoldersAsync(
                    client.PersonalNamespaces[0], StatusItems.None, subscribedOnly: false, scope.Token)
                    .ConfigureAwait(false);
            }

            return (IReadOnlyList<FolderDescriptor>)folders
                .OrderBy(folder => folder.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(ToFolderDescriptor)
                .ToArray();
        }, cancellationToken);

    public Task<MessagePage> ListMessagesAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        string folderId,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidatePaging(folderId, offset, pageSize);
        return WithClientAsync(profile, credential, "message_list", async client =>
        {
            IMailFolder folder = GetFolder(client, folderId, cancellationToken);
            await OpenAsync(folder, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
            uint uidValidity = RequireUidValidity(folder);
            return await SearchAndFetchPageAsync(
                profile.Id, folder, folderId, uidValidity, SearchQuery.All,
                offset, pageSize, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

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
        ValidatePaging(folderId, offset, pageSize);
        SearchQuery query = ImapSearchQueryBuilder.Build(criteria);

        return WithClientAsync(profile, credential, "message_search", async client =>
        {
            IMailFolder folder = GetFolder(client, folderId, cancellationToken);
            await OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            uint uidValidity = RequireUidValidity(folder);
            return await SearchAndFetchPageAsync(
                profile.Id, folder, folderId, uidValidity, query,
                offset, pageSize, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<MessageContent> ReadAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        bool markAsRead,
        BodyMode bodyMode,
        CancellationToken cancellationToken)
    {
        ValidateReference(profile, reference);
        if (!Enum.IsDefined(bodyMode))
            throw new ArgumentOutOfRangeException(nameof(bodyMode));

        return WithClientAsync(profile, credential, "message_read", async client =>
        {
            string folderId = reference.FolderId!;
            uint expectedUidValidity = reference.UidValidity!.Value;
            var uid = new UniqueId(reference.Uid!.Value);
            IMailFolder folder = GetFolder(client, folderId, cancellationToken);
            await OpenAsync(
                folder,
                markAsRead ? FolderAccess.ReadWrite : FolderAccess.ReadOnly,
                cancellationToken).ConfigureAwait(false);
            EnsureUidValidity(folder, expectedUidValidity, profile.Id, folderId);

            MimeMessage message;
            using (var scope = CreateScope(cancellationToken))
            {
                message = await folder.GetMessageAsync(uid, scope.Token).ConfigureAwait(false);
            }

            MessageContent converted = contentService.Convert(message, bodyMode, maxBodyCharacters);
            if (!markAsRead)
            {
                return converted with
                {
                    ReadStateSupported = true,
                    IsRead = null,
                    ReadStateUpdated = false
                };
            }

            EnsureUidValidity(folder, expectedUidValidity, profile.Id, folderId);
            if (folder.Access != FolderAccess.ReadWrite)
                return WithSeenUpdateWarning(converted);

            try
            {
                using var scope = CreateScope(cancellationToken);
                await folder.AddFlagsAsync(uid, MessageFlags.Seen, silent: true, scope.Token)
                    .ConfigureAwait(false);
                return converted with
                {
                    ReadStateSupported = true,
                    IsRead = true,
                    ReadStateUpdated = true
                };
            }
            catch (CommandException)
            {
                return WithSeenUpdateWarning(converted);
            }
        }, cancellationToken);
    }

    public Task<int> MarkReadAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        IReadOnlyList<MessageReference> references,
        bool isRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        foreach (MessageReference reference in references)
            ValidateReference(profile, reference);
        if (references.Count == 0)
            return Task.FromResult(0);

        return WithClientAsync(profile, credential, "message_mark_read", async client =>
        {
            int updated = 0;
            foreach (IGrouping<(string FolderId, uint UidValidity), MessageReference> group in references.GroupBy(
                         reference => (reference.FolderId!, reference.UidValidity!.Value)))
            {
                IMailFolder folder = GetFolder(client, group.Key.FolderId, cancellationToken);
                await OpenAsync(folder, FolderAccess.ReadWrite, cancellationToken).ConfigureAwait(false);
                EnsureUidValidity(folder, group.Key.UidValidity, profile.Id, group.Key.FolderId);

                UniqueId[] uids = group
                    .Select(reference => new UniqueId(reference.Uid!.Value))
                    .Distinct()
                    .OrderBy(uid => uid.Id)
                    .ToArray();

                EnsureUidValidity(folder, group.Key.UidValidity, profile.Id, group.Key.FolderId);
                using var scope = CreateScope(cancellationToken);
                if (isRead)
                {
                    await folder.AddFlagsAsync(uids, MessageFlags.Seen, silent: true, scope.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    await folder.RemoveFlagsAsync(uids, MessageFlags.Seen, silent: true, scope.Token)
                        .ConfigureAwait(false);
                }

                updated += uids.Length;
            }

            return updated;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        ValidateReference(profile, reference);
        return WithClientAsync(profile, credential, "attachment_list", async client =>
        {
            (IMailFolder folder, UniqueId uid) = await OpenReferencedFolderAsync(
                client, profile, reference, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<StructuredAttachment> attachments = await GetStructuredAttachmentsAsync(
                folder, uid, profile, reference, cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<AttachmentDescriptor>)attachments
                .Select(item => item.Descriptor)
                .ToArray();
        }, cancellationToken);
    }

    public Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        ValidateReference(profile, reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);
        return WithClientAsync(profile, credential, "attachment_save", async client =>
        {
            (IMailFolder folder, UniqueId uid) = await OpenReferencedFolderAsync(
                client, profile, reference, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<StructuredAttachment> attachments = await GetStructuredAttachmentsAsync(
                folder, uid, profile, reference, cancellationToken).ConfigureAwait(false);
            StructuredAttachment? selected = attachments.FirstOrDefault(item =>
                string.Equals(item.Descriptor.Id, attachmentId, StringComparison.Ordinal));
            if (selected is null)
                throw AttachmentNotFound();

            if (selected.Part.PartSpecifier.Length == 0)
            {
                Stream content = await GetTopLevelBodyContentAsync(
                    folder, uid, selected.Part, cancellationToken)
                    .ConfigureAwait(false);
                return new OpenedAttachment(selected.Descriptor, content);
            }

            MimeEntity entity;
            using (var scope = CreateScope(cancellationToken))
            {
                entity = await folder.GetBodyPartAsync(uid, selected.Part, scope.Token)
                    .ConfigureAwait(false);
            }
            using (entity)
            {
                Stream content = await DecodeAttachmentAsync(entity, cancellationToken)
                    .ConfigureAwait(false);
                return new OpenedAttachment(selected.Descriptor, content);
            }
        }, cancellationToken);
    }

    private async Task<Stream> GetTopLevelBodyContentAsync(
        IMailFolder folder,
        UniqueId uid,
        BodyPartBasic part,
        CancellationToken cancellationToken)
    {
        Stream encoded;
        using (var scope = CreateScope(cancellationToken))
        {
            encoded = await folder.GetStreamAsync(uid, "TEXT", scope.Token)
                .ConfigureAwait(false);
        }

        await using (encoded)
        {
            ContentEncoding encoding;
            if (string.IsNullOrEmpty(part.ContentTransferEncoding) ||
                !MimeUtils.TryParse(part.ContentTransferEncoding, out encoding))
            {
                encoding = ContentEncoding.Default;
            }

            var output = new MemoryStream();
            try
            {
                using var content = new MimeContent(encoded, encoding);
                using var scope = CreateScope(cancellationToken);
                await content.DecodeToAsync(output, scope.Token).ConfigureAwait(false);
                output.Position = 0;
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
    }

    public Task<Stream> OpenAttachmentAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        string attachmentId,
        CancellationToken cancellationToken)
    {
        ValidateReference(profile, reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);

        return WithClientAsync(profile, credential, "attachment_save", async client =>
        {
            string folderId = reference.FolderId!;
            uint expectedUidValidity = reference.UidValidity!.Value;
            IMailFolder folder = GetFolder(client, folderId, cancellationToken);
            await OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            EnsureUidValidity(folder, expectedUidValidity, profile.Id, folderId);

            MimeMessage message;
            using (var scope = CreateScope(cancellationToken))
            {
                message = await folder.GetMessageAsync(
                    new UniqueId(reference.Uid!.Value), scope.Token).ConfigureAwait(false);
            }

            MimeEntity? attachment = partLocator.Find(message, attachmentId);
            if (attachment is null || !IsAttachment(attachment))
            {
                throw new MailOperationException(new ToolError(
                    "attachment.not_found",
                    ErrorCategory.Validation,
                    "The requested attachment was not found.",
                    false,
                    null,
                    null));
            }

            var output = new MemoryStream();
            using (var scope = CreateScope(cancellationToken))
            {
                switch (attachment)
                {
                    case MimePart { Content: not null } part:
                        await part.Content.DecodeToAsync(output, scope.Token).ConfigureAwait(false);
                        break;
                    case MessagePart { Message: not null } messagePart:
                        await messagePart.Message.WriteToAsync(output, scope.Token).ConfigureAwait(false);
                        break;
                    default:
                        throw new MailOperationException(new ToolError(
                            "attachment.not_found",
                            ErrorCategory.Validation,
                            "The requested attachment was not found.",
                            false,
                            null,
                            null));
                }
            }
            output.Position = 0;
            return (Stream)output;
        }, cancellationToken);
    }

    private async Task<(IMailFolder Folder, UniqueId Uid)> OpenReferencedFolderAsync(
        ImapClient client,
        AccountProfile profile,
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        string folderId = reference.FolderId!;
        IMailFolder folder = GetFolder(client, folderId, cancellationToken);
        await OpenAsync(folder, FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
        EnsureUidValidity(folder, reference.UidValidity!.Value, profile.Id, folderId);
        return (folder, new UniqueId(reference.Uid!.Value));
    }

    private async Task<IReadOnlyList<StructuredAttachment>> GetStructuredAttachmentsAsync(
        IMailFolder folder,
        UniqueId uid,
        AccountProfile profile,
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        IList<IMessageSummary> summaries;
        using (var scope = CreateScope(cancellationToken))
        {
            summaries = await folder.FetchAsync(
                new[] { uid },
                MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure,
                scope.Token).ConfigureAwait(false);
        }

        IMessageSummary? summary = summaries.FirstOrDefault(item => item.UniqueId == uid);
        if (summary is null)
            throw ReferenceConflict(profile.Id, reference.FolderId!);

        return summary.BodyParts
            .Select((part, index) => new { Part = part, Index = index })
            .Where(item => IsAttachment(item.Part))
            .Select(item => new StructuredAttachment(
                item.Part,
                new AttachmentDescriptor(
                    $"part-{item.Index + 1}",
                    item.Part.FileName,
                    item.Part.ContentType.MimeType.ToLowerInvariant(),
                    item.Part.Octets,
                    string.Equals(
                        item.Part.ContentDisposition?.Disposition,
                        ContentDisposition.Inline,
                        StringComparison.OrdinalIgnoreCase),
                    item.Part.ContentId)))
            .ToArray();
    }

    private async Task<Stream> DecodeAttachmentAsync(
        MimeEntity attachment,
        CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        try
        {
            using var scope = CreateScope(cancellationToken);
            switch (attachment)
            {
                case MimePart { Content: not null } part:
                    await part.Content.DecodeToAsync(output, scope.Token).ConfigureAwait(false);
                    break;
                case MessagePart { Message: not null } messagePart:
                    await messagePart.Message.WriteToAsync(output, scope.Token).ConfigureAwait(false);
                    break;
                default:
                    throw AttachmentNotFound();
            }
            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private async Task<MessagePage> SearchAndFetchPageAsync(
        string accountId,
        IMailFolder folder,
        string folderId,
        uint uidValidity,
        SearchQuery query,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        IList<UniqueId> matches;
        using (var scope = CreateScope(cancellationToken))
        {
            matches = await folder.SearchAsync(query, scope.Token).ConfigureAwait(false);
        }

        UniqueId[] ordered = matches
            .Where(uid => uid.IsValid)
            .Distinct()
            .OrderByDescending(uid => uid.Id)
            .ToArray();
        UniqueId[] pageUids = ordered.Skip(offset).Take(pageSize).ToArray();

        if (pageUids.Length == 0)
            return new MessagePage(Array.Empty<MessageEnvelope>(), null);

        IList<IMessageSummary> summaries;
        using (var scope = CreateScope(cancellationToken))
        {
            summaries = await folder.FetchAsync(pageUids, SummaryItems, scope.Token)
                .ConfigureAwait(false);
        }

        Dictionary<uint, IMessageSummary> byUid = summaries
            .Where(summary => summary.UniqueId.IsValid)
            .ToDictionary(summary => summary.UniqueId.Id);
        MessageEnvelope[] messages = pageUids
            .Where(uid => byUid.ContainsKey(uid.Id))
            .Select(uid => ToMessageEnvelope(
                accountId, folderId, uidValidity, byUid[uid.Id]))
            .ToArray();
        int? nextOffset = offset + pageUids.Length < ordered.Length
            ? offset + messages.Length
            : null;

        return new MessagePage(messages, nextOffset);
    }

    private async Task<T> WithClientAsync<T>(
        AccountProfile profile,
        PasswordCredentialLease credential,
        string operation,
        Func<ImapClient, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        // The per-account/IMAP lease is held from just before connect until the
        // disconnect/dispose cleanup completes; the gate queues callers instead of
        // failing them.
        IAsyncDisposable lease = await connectionGate
            .AcquireAsync(profile.Id, "imap", cancellationToken)
            .ConfigureAwait(false);
        ImapClient? client = null;
        try
        {
            client = await clientFactory.CreateAsync(profile, credential, cancellationToken)
                .ConfigureAwait(false);
            return await action(client).ConfigureAwait(false);
        }
        catch (MailOperationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ProtocolExceptionMapper.Map(
                exception, "imap", operation, cancellationToken);
        }
        finally
        {
            if (client is not null)
                await DisconnectAndDisposeAsync(client).ConfigureAwait(false);
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisconnectAndDisposeAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var scope = CommandTimeoutScope.Create(commandTimeout, CancellationToken.None);
                await client.DisconnectAsync(true, scope.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Cleanup cannot replace the stable result of the requested operation.
        }
        finally
        {
            client.Dispose();
        }
    }

    private IMailFolder GetFolder(
        ImapClient client,
        string folderId,
        CancellationToken cancellationToken)
    {
        using var scope = CreateScope(cancellationToken);
        return client.GetFolder(folderId, scope.Token);
    }

    private async Task OpenAsync(
        IMailFolder folder,
        FolderAccess access,
        CancellationToken cancellationToken)
    {
        using var scope = CreateScope(cancellationToken);
        await folder.OpenAsync(access, scope.Token).ConfigureAwait(false);
    }

    private CommandTimeoutScope CreateScope(CancellationToken cancellationToken) =>
        CommandTimeoutScope.Create(commandTimeout, cancellationToken);

    private static uint RequireUidValidity(IMailFolder folder)
    {
        if (folder.UidValidity > 0)
            return folder.UidValidity;

        throw CapabilityError(
            "imap.uidvalidity_unavailable",
            "The IMAP folder did not provide a stable UIDVALIDITY value.");
    }

    private static void EnsureUidValidity(
        IMailFolder folder,
        uint expected,
        string accountId,
        string folderId)
    {
        if (folder.UidValidity == expected && expected > 0)
            return;

        throw new MailOperationException(new ToolError(
            "message.reference_conflict",
            ErrorCategory.Conflict,
            "The message reference no longer identifies the same IMAP folder state.",
            false,
            null,
            new Dictionary<string, string>
            {
                ["account_id"] = accountId,
                ["folder_id"] = folderId
            }));
    }

    private static FolderDescriptor ToFolderDescriptor(IMailFolder folder) =>
        new(
            folder.FullName,
            folder.Name,
            (folder.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0,
            GetAttributeNames(folder.Attributes),
            GetSpecialUse(folder.Attributes));

    private static MessageEnvelope ToMessageEnvelope(
        string accountId,
        string folderId,
        uint uidValidity,
        IMessageSummary summary) =>
        new(
            MessageReference.ForImap(accountId, folderId, uidValidity, summary.UniqueId.Id),
            summary.Envelope?.Subject,
            summary.Envelope?.From.Select(address => address.ToString()).ToArray() ?? Array.Empty<string>(),
            summary.Envelope?.To.Select(address => address.ToString()).ToArray() ?? Array.Empty<string>(),
            summary.Envelope?.Date,
            summary.InternalDate,
            summary.Size,
            GetFlagNames(summary.Flags),
            summary.Attachments.Any());

    private static IReadOnlyList<string> GetAttributeNames(FolderAttributes attributes) =>
        Enum.GetValues<FolderAttributes>()
            .Where(value => value != FolderAttributes.None && attributes.HasFlag(value))
            .Select(value => ToSnakeCase(value.ToString()))
            .ToArray();

    private static IReadOnlyList<string> GetFlagNames(MessageFlags? flags)
    {
        if (!flags.HasValue)
            return Array.Empty<string>();

        return Enum.GetValues<MessageFlags>()
            .Where(value => value != MessageFlags.None && IsSingleFlag(value) && flags.Value.HasFlag(value))
            .Select(value => ToSnakeCase(value.ToString()))
            .ToArray();
    }

    private static bool IsSingleFlag(MessageFlags value)
    {
        int number = (int)value;
        return number > 0 && (number & (number - 1)) == 0;
    }

    private static string? GetSpecialUse(FolderAttributes attributes)
    {
        (FolderAttributes Attribute, string Name)[] known =
        {
            (FolderAttributes.Inbox, "inbox"),
            (FolderAttributes.All, "all"),
            (FolderAttributes.Archive, "archive"),
            (FolderAttributes.Drafts, "drafts"),
            (FolderAttributes.Flagged, "flagged"),
            (FolderAttributes.Important, "important"),
            (FolderAttributes.Junk, "junk"),
            (FolderAttributes.Sent, "sent"),
            (FolderAttributes.Trash, "trash")
        };
        return known.FirstOrDefault(item => attributes.HasFlag(item.Attribute)).Name;
    }

    private static string ToSnakeCase(string value)
    {
        var output = new System.Text.StringBuilder(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
                output.Append('_');
            output.Append(char.ToLowerInvariant(character));
        }
        return output.ToString();
    }

    private static bool IsAttachment(MimeEntity entity) =>
        entity.IsAttachment ||
        !string.IsNullOrWhiteSpace(entity.ContentDisposition?.FileName) ||
        !string.IsNullOrWhiteSpace(entity.ContentType.Name);

    private static bool IsAttachment(BodyPartBasic part) =>
        part.IsAttachment || !string.IsNullOrWhiteSpace(part.FileName);

    private static MessageContent WithSeenUpdateWarning(MessageContent converted) =>
        converted with
        {
            ReadStateSupported = true,
            IsRead = null,
            ReadStateUpdated = false,
            Warnings = new[]
            {
                new ToolError(
                    "imap.seen_update_failed",
                    ErrorCategory.Authorization,
                    "The message was read, but its read state could not be updated.",
                    false,
                    null,
                    new Dictionary<string, string> { ["protocol"] = "imap" })
            }
        };

    private static void ValidatePaging(string folderId, int offset, int pageSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderId);
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
    }

    private static void ValidateReference(AccountProfile profile, MessageReference reference)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Protocol != MailProtocol.Imap ||
            !string.Equals(reference.AccountId, profile.Id, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference.FolderId) ||
            reference.UidValidity is null or 0 ||
            reference.Uid is null or 0)
        {
            throw new MailOperationException(new ToolError(
                "message.reference_conflict",
                ErrorCategory.Conflict,
                "The message reference does not match the requested IMAP account.",
                false,
                null,
                null));
        }
    }

    private static MailOperationException CapabilityError(string code, string message) =>
        new(new ToolError(
            code,
            ErrorCategory.Capability,
            message,
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "imap" }));

    private static MailOperationException ReferenceConflict(string accountId, string folderId) =>
        new(new ToolError(
            "message.reference_conflict",
            ErrorCategory.Conflict,
            "The message reference no longer identifies an IMAP message.",
            false,
            null,
            new Dictionary<string, string>
            {
                ["account_id"] = accountId,
                ["folder_id"] = folderId
            }));

    private static MailOperationException AttachmentNotFound() =>
        new(new ToolError(
            "attachment.not_found",
            ErrorCategory.Validation,
            "The requested attachment was not found.",
            false,
            null,
            null));

    private sealed record StructuredAttachment(
        BodyPartBasic Part,
        AttachmentDescriptor Descriptor);
}
