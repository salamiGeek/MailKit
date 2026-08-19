using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Core.Mail;

public interface IPop3Gateway
{
    Task<MessagePage> ListMessagesAsync(AccountProfile profile,
        PasswordCredentialLease credential, int offset, int pageSize,
        CancellationToken cancellationToken);

    Task<MessageContent> ReadAsync(AccountProfile profile, PasswordCredentialLease credential,
        MessageReference reference, BodyMode bodyMode, CancellationToken cancellationToken);

    Task<IReadOnlyList<AttachmentDescriptor>> ListAttachmentsAsync(
        AccountProfile profile, PasswordCredentialLease credential,
        MessageReference reference, CancellationToken cancellationToken);

    Task<OpenedAttachment> OpenAttachmentWithDescriptorAsync(
        AccountProfile profile, PasswordCredentialLease credential,
        MessageReference reference, string attachmentId,
        CancellationToken cancellationToken);

    Task<Stream> OpenAttachmentAsync(AccountProfile profile, PasswordCredentialLease credential,
        MessageReference reference, string attachmentId, CancellationToken cancellationToken);
}
