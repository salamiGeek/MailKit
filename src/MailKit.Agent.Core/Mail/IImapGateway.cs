using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Core.Mail;

public interface IImapGateway
{
    Task<IReadOnlyList<FolderDescriptor>> ListFoldersAsync(AccountProfile profile,
        PasswordCredentialLease credential, CancellationToken cancellationToken);

    Task<MessagePage> ListMessagesAsync(AccountProfile profile, PasswordCredentialLease credential,
        string folderId, int offset, int pageSize, CancellationToken cancellationToken);

    Task<MessagePage> SearchAsync(AccountProfile profile, PasswordCredentialLease credential,
        string folderId, MessageSearchCriteria criteria, int offset, int pageSize,
        CancellationToken cancellationToken);

    Task<MessageContent> ReadAsync(AccountProfile profile, PasswordCredentialLease credential,
        MessageReference reference, bool markAsRead, BodyMode bodyMode,
        CancellationToken cancellationToken);

    Task<int> MarkReadAsync(AccountProfile profile, PasswordCredentialLease credential,
        IReadOnlyList<MessageReference> references, bool isRead,
        CancellationToken cancellationToken);

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
