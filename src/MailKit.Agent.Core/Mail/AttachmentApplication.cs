using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Mail;

public sealed class AttachmentApplication
{
    private readonly AccountOperationBoundary boundary;
    private readonly IImapGateway imapGateway;
    private readonly IPop3Gateway pop3Gateway;
    private readonly IAttachmentWriter writer;

    public AttachmentApplication(
        IAccountProfileStore accountStore,
        IAccountCredentialVault credentialVault,
        IImapGateway imapGateway,
        IPop3Gateway pop3Gateway,
        IAttachmentWriter writer,
        OperationPolicy policy)
    {
        boundary = new AccountOperationBoundary(accountStore, credentialVault, policy);
        this.imapGateway = imapGateway ?? throw new ArgumentNullException(nameof(imapGateway));
        this.pop3Gateway = pop3Gateway ?? throw new ArgumentNullException(nameof(pop3Gateway));
        this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public Task<ToolResult<IReadOnlyList<AttachmentDescriptor>>> ListAsync(
        MessageReference reference,
        CancellationToken cancellationToken)
    {
        if (!TryGetProtocol(reference, out string? protocol))
            return Task.FromResult(ReferenceFailure<IReadOnlyList<AttachmentDescriptor>>());

        return boundary.ExecuteAsync(
            reference.AccountId,
            protocol!,
            "attachment_list",
            RiskLevel.ReadOnly,
            1,
            (profile, credential) => reference.Protocol == MailProtocol.Imap
                ? imapGateway.ListAttachmentsAsync(
                    profile, credential, reference, cancellationToken)
                : pop3Gateway.ListAttachmentsAsync(
                    profile, credential, reference, cancellationToken),
            cancellationToken,
            attachments => Math.Max(1, attachments.Count));
    }

    public Task<ToolResult<AttachmentSaveResult>> SaveAsync(
        MessageReference reference,
        string attachmentId,
        string? destinationName,
        CancellationToken cancellationToken)
    {
        if (!TryGetProtocol(reference, out string? protocol))
            return Task.FromResult(ReferenceFailure<AttachmentSaveResult>());
        if (string.IsNullOrWhiteSpace(attachmentId))
        {
            return Task.FromResult(ToolResult<AttachmentSaveResult>.Failure(
                AttachmentNotFound(), Guid.NewGuid().ToString("N")));
        }

        return boundary.ExecuteAsync(
            reference.AccountId,
            protocol!,
            "attachment_save",
            RiskLevel.RecoverableWrite,
            1,
            async (profile, credential) =>
            {
                OpenedAttachment opened = reference.Protocol == MailProtocol.Imap
                    ? await imapGateway.OpenAttachmentWithDescriptorAsync(
                        profile, credential, reference, attachmentId, cancellationToken)
                        .ConfigureAwait(false)
                    : await pop3Gateway.OpenAttachmentWithDescriptorAsync(
                        profile, credential, reference, attachmentId, cancellationToken)
                        .ConfigureAwait(false);
                if (!string.Equals(opened.Descriptor.Id, attachmentId, StringComparison.Ordinal))
                {
                    await opened.Content.DisposeAsync().ConfigureAwait(false);
                    throw new MailOperationException(AttachmentNotFound());
                }

                await using Stream source = opened.Content;
                return await writer.SaveAsync(
                    source, opened.Descriptor, destinationName, cancellationToken)
                    .ConfigureAwait(false);
            },
            cancellationToken);
    }

    private static bool TryGetProtocol(MessageReference? reference, out string? protocol)
    {
        protocol = reference?.Protocol switch
        {
            MailProtocol.Imap when
                AccountProfileValidator.ValidateId(reference.AccountId) &&
                !string.IsNullOrWhiteSpace(reference.FolderId) &&
                reference.UidValidity is > 0 && reference.Uid is > 0 && reference.Uidl is null => "imap",
            MailProtocol.Pop3 when
                AccountProfileValidator.ValidateId(reference.AccountId) &&
                !string.IsNullOrWhiteSpace(reference.Uidl) && reference.FolderId is null &&
                reference.UidValidity is null && reference.Uid is null => "pop3",
            _ => null
        };
        return protocol is not null;
    }

    private static ToolResult<T> ReferenceFailure<T>() =>
        ToolResult<T>.Failure(
            new ToolError(
                "message.reference_conflict", ErrorCategory.Conflict,
                "The message reference is invalid or does not match the requested account.",
                false, null, null),
            Guid.NewGuid().ToString("N"));

    private static ToolError AttachmentNotFound() => new(
        "attachment.not_found",
        ErrorCategory.Validation,
        "The requested attachment was not found.",
        false,
        null,
        null);
}
