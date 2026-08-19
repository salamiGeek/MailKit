namespace MailKit.Agent.Core.Mail;

public sealed record OpenedAttachment(
    AttachmentDescriptor Descriptor,
    Stream Content);
