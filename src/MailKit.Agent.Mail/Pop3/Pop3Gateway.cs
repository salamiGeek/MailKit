using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Mail.Connections;
using MailKit.Agent.Mail.Mime;
using MailKit.Net.Pop3;
using MimeKit;

namespace MailKit.Agent.Mail.Pop3;

public sealed class Pop3Gateway : IPop3Gateway
{
    private readonly IPop3ClientFactory clientFactory;
    private readonly MimeContentService contentService;
    private readonly MimePartLocator partLocator;
    private readonly FailedServiceCleanupOwner cleanupOwner = new();
    private readonly TimeSpan commandTimeout;
    private readonly int maxBodyCharacters;

    public Pop3Gateway()
        : this(new Pop3ClientFactory())
    {
    }

    public Pop3Gateway(
        IPop3ClientFactory clientFactory,
        MimeContentService? contentService = null,
        MimePartLocator? partLocator = null,
        TimeSpan? commandTimeout = null,
        int maxBodyCharacters = 100_000)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.contentService = contentService ?? new MimeContentService();
        this.partLocator = partLocator ?? new MimePartLocator();
        this.commandTimeout = commandTimeout ?? ConnectionLimits.Default.CommandTimeout;
        if (this.commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
        if (maxBodyCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBodyCharacters));
        this.maxBodyCharacters = maxBodyCharacters;
    }

    public Task<MessagePage> ListMessagesAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        int offset,
        int pageSize,
        CancellationToken cancellationToken)
    {
        ValidatePaging(offset, pageSize);
        return WithClientAsync(profile, credential, "pop3_message_list", async client =>
        {
            IReadOnlyList<string> uidls = await LoadStableUidlsAsync(client, cancellationToken)
                .ConfigureAwait(false);
            int[] pageIndexes = Enumerable.Range(0, uidls.Count)
                .Skip(offset)
                .Take(pageSize)
                .ToArray();
            if (pageIndexes.Length == 0)
                return new MessagePage(Array.Empty<MessageEnvelope>(), null);

            IList<int> sizes;
            using (var scope = CreateScope(cancellationToken))
            {
                sizes = await client.GetMessageSizesAsync(scope.Token).ConfigureAwait(false);
            }
            if (sizes.Count != uidls.Count)
                throw UidlConflict();

            var messages = new List<MessageEnvelope>(pageIndexes.Length);
            foreach (int index in pageIndexes)
            {
                if ((client.Capabilities & Pop3Capabilities.Top) != 0)
                {
                    HeaderList headers;
                    using (var scope = CreateScope(cancellationToken))
                    {
                        headers = await client.GetMessageHeadersAsync(index, scope.Token)
                            .ConfigureAwait(false);
                    }
                    messages.Add(ToEnvelope(profile.Id, uidls[index], sizes[index], headers));
                }
                else
                {
                    MimeMessage message;
                    using (var scope = CreateScope(cancellationToken))
                    {
                        message = await client.GetMessageAsync(index, scope.Token)
                            .ConfigureAwait(false);
                    }
                    using (message)
                    {
                        messages.Add(ToEnvelope(
                            profile.Id, uidls[index], sizes[index], message.Headers));
                    }
                }
            }

            int? nextOffset = offset + pageIndexes.Length < uidls.Count
                ? offset + pageIndexes.Length
                : null;
            return new MessagePage(messages, nextOffset);
        }, cancellationToken);
    }

    public Task<MessageContent> ReadAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        MessageReference reference,
        BodyMode bodyMode,
        CancellationToken cancellationToken)
    {
        ValidateReference(profile, reference);
        if (!Enum.IsDefined(bodyMode))
            throw new ArgumentOutOfRangeException(nameof(bodyMode));

        return WithClientAsync(profile, credential, "pop3_message_read", async client =>
        {
            int index = await ResolveCurrentIndexAsync(client, reference.Uidl!, cancellationToken)
                .ConfigureAwait(false);
            MimeMessage message;
            using (var scope = CreateScope(cancellationToken))
            {
                message = await client.GetMessageAsync(index, scope.Token).ConfigureAwait(false);
            }
            using (message)
            {
                MessageContent converted = contentService.Convert(
                    message, bodyMode, maxBodyCharacters);
                return converted with
                {
                    ReadStateSupported = false,
                    IsRead = null,
                    ReadStateUpdated = false
                };
            }
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
            using MimeMessage message = await GetReferencedMessageAsync(
                client, reference, cancellationToken).ConfigureAwait(false);
            return contentService.Convert(message, BodyMode.SafeText, maxBodyCharacters).Attachments;
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
            using MimeMessage message = await GetReferencedMessageAsync(
                client, reference, cancellationToken).ConfigureAwait(false);
            AttachmentDescriptor? descriptor = contentService
                .Convert(message, BodyMode.SafeText, maxBodyCharacters)
                .Attachments
                .FirstOrDefault(item => string.Equals(
                    item.Id, attachmentId, StringComparison.Ordinal));
            MimeEntity? attachment = partLocator.Find(message, attachmentId);
            if (descriptor is null || attachment is null || !IsAttachment(attachment))
                throw AttachmentNotFound();

            Stream content = await DecodeAttachmentAsync(attachment, cancellationToken)
                .ConfigureAwait(false);
            return new OpenedAttachment(descriptor, content);
        }, cancellationToken);
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
            int index = await ResolveCurrentIndexAsync(client, reference.Uidl!, cancellationToken)
                .ConfigureAwait(false);
            MimeMessage message;
            using (var scope = CreateScope(cancellationToken))
            {
                message = await client.GetMessageAsync(index, scope.Token).ConfigureAwait(false);
            }
            using (message)
            {
                MimeEntity? attachment = partLocator.Find(message, attachmentId);
                if (attachment is null || !IsAttachment(attachment))
                    throw AttachmentNotFound();

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
                    return (Stream)output;
                }
                catch
                {
                    output.Dispose();
                    throw;
                }
            }
        }, cancellationToken);
    }

    private async Task<MimeMessage> GetReferencedMessageAsync(
        Pop3Client client,
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        int index = await ResolveCurrentIndexAsync(client, reference.Uidl!, cancellationToken)
            .ConfigureAwait(false);
        using var scope = CreateScope(cancellationToken);
        return await client.GetMessageAsync(index, scope.Token).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<string>> LoadStableUidlsAsync(
        Pop3Client client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsUids)
            throw UidlRequired();

        IList<string> uidls;
        try
        {
            using var scope = CreateScope(cancellationToken);
            uidls = await client.GetMessageUidsAsync(scope.Token).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            throw UidlRequired();
        }
        catch (Pop3CommandException)
        {
            throw UidlRequired();
        }
        catch (Pop3ProtocolException) when (client.IsConnected)
        {
            throw UidlConflict();
        }

        if (uidls.Count != client.Count ||
            uidls.Any(string.IsNullOrWhiteSpace) ||
            uidls.Distinct(StringComparer.Ordinal).Count() != uidls.Count)
        {
            throw UidlConflict();
        }

        return uidls.ToArray();
    }

    private async Task<int> ResolveCurrentIndexAsync(
        Pop3Client client,
        string expectedUidl,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> uidls = await LoadStableUidlsAsync(client, cancellationToken)
            .ConfigureAwait(false);
        for (int index = 0; index < uidls.Count; index++)
        {
            if (string.Equals(uidls[index], expectedUidl, StringComparison.Ordinal))
                return index;
        }

        throw ReferenceConflict();
    }

    private async Task<T> WithClientAsync<T>(
        AccountProfile profile,
        PasswordCredentialLease credential,
        string operation,
        Func<Pop3Client, Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);

        Pop3Client? client = null;
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
                exception, "pop3", operation, cancellationToken);
        }
        finally
        {
            if (client is not null)
                await DisconnectAndDisposeAsync(client).ConfigureAwait(false);
        }
    }

    private async Task DisconnectAndDisposeAsync(Pop3Client client)
    {
        Task? disconnectTask = null;
        try
        {
            if (client.IsConnected)
            {
                using var scope = CommandTimeoutScope.Create(
                    commandTimeout, CancellationToken.None);
                disconnectTask = client.DisconnectAsync(true, scope.Token);
                await disconnectTask.WaitAsync(scope.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if (disconnectTask is not null && !disconnectTask.IsCompletedSuccessfully)
                cleanupOwner.Own(client, disconnectTask);

            // Cleanup cannot replace the stable result of the requested operation.
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // Cleanup cannot replace the stable result of the requested operation.
            }
        }
    }

    private CommandTimeoutScope CreateScope(CancellationToken cancellationToken) =>
        CommandTimeoutScope.Create(commandTimeout, cancellationToken);

    private static MessageEnvelope ToEnvelope(
        string accountId,
        string uidl,
        int size,
        HeaderList headers)
    {
        using var message = new MimeMessage(headers);
        DateTimeOffset? date = headers.Contains(HeaderId.Date) ? message.Date : null;
        return new MessageEnvelope(
            MessageReference.ForPop3(accountId, uidl),
            message.Subject,
            message.From.Select(address => address.ToString()).ToArray(),
            message.To.Select(address => address.ToString()).ToArray(),
            date,
            null,
            checked((uint)size),
            Array.Empty<string>(),
            false);
    }

    private static bool IsAttachment(MimeEntity entity) =>
        entity.IsAttachment ||
        !string.IsNullOrWhiteSpace(entity.ContentDisposition?.FileName) ||
        !string.IsNullOrWhiteSpace(entity.ContentType.Name);

    private static void ValidatePaging(int offset, int pageSize)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
    }

    private static void ValidateReference(AccountProfile profile, MessageReference reference)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Protocol != MailProtocol.Pop3 ||
            !string.Equals(reference.AccountId, profile.Id, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(reference.Uidl) ||
            reference.FolderId is not null ||
            reference.UidValidity is not null ||
            reference.Uid is not null)
        {
            throw ReferenceConflict();
        }
    }

    private static MailOperationException UidlRequired() =>
        new(new ToolError(
            "pop3.uidl_required",
            ErrorCategory.Capability,
            "The POP3 server must support UIDL for stable message references.",
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "pop3" }));

    private static MailOperationException UidlConflict() =>
        new(new ToolError(
            "pop3.uidl_conflict",
            ErrorCategory.Conflict,
            "The POP3 server did not provide one unique UIDL for every message.",
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "pop3" }));

    private static MailOperationException ReferenceConflict() =>
        new(new ToolError(
            "message.reference_conflict",
            ErrorCategory.Conflict,
            "The message reference no longer identifies a POP3 message in this account.",
            false,
            null,
            new Dictionary<string, string> { ["protocol"] = "pop3" }));

    private static MailOperationException AttachmentNotFound() =>
        new(new ToolError(
            "attachment.not_found",
            ErrorCategory.Validation,
            "The requested attachment was not found.",
            false,
            null,
            null));
}
