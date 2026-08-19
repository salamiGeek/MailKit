namespace MailKit.Agent.Mail.Attachments;

public sealed class UploadAttachmentPathPolicy
{
    private readonly IReadOnlyList<string> roots;
    private readonly StringComparison pathComparison;

    public UploadAttachmentPathPolicy(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        this.roots = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .ToArray();
        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string ResolveForRead(string requestedPath)
    {
        if (roots.Count == 0)
        {
            throw AttachmentPathPolicy.CreatePolicyError(
                "attachment.upload_roots_required",
                "SMTP attachments require an explicitly configured upload root.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(requestedPath);
        if (!Path.IsPathRooted(requestedPath))
        {
            throw AttachmentPathPolicy.CreatePolicyError(
                "attachment.upload_path_not_allowed",
                "The SMTP attachment path is outside the configured upload roots.");
        }

        string fullPath = Path.GetFullPath(requestedPath);
        foreach (string root in roots)
        {
            string rootWithSeparator = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootWithSeparator, pathComparison))
                return fullPath;
        }

        throw AttachmentPathPolicy.CreatePolicyError(
            "attachment.upload_path_not_allowed",
            "The SMTP attachment path is outside the configured upload roots.");
    }
}
