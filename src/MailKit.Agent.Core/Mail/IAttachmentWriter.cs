namespace MailKit.Agent.Core.Mail;

public interface IAttachmentWriter
{
    Task<AttachmentSaveResult> SaveAsync(
        Stream source,
        AttachmentDescriptor descriptor,
        string? destinationName,
        CancellationToken cancellationToken);
}
