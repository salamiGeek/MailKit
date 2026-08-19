namespace MailKit.Agent.Core.Policy;

public sealed record MailSafetyLimits(
    int MaxBodyCharacters,
    long MaxAttachmentBytes,
    long MaxDownloadBytesPerCall,
    int MaxPageSize)
{
    public static MailSafetyLimits Default { get; } =
        new(200_000, 25 * 1024 * 1024, 50 * 1024 * 1024, 100);
}
