namespace MailKit.Agent.Core.Storage;

public static class MailFileOptionsResolver
{
    public static MailFileOptions Resolve(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        string? configuredDownload = Environment.GetEnvironmentVariable("MAILKIT_AGENT_DOWNLOAD_ROOT");
        string downloadRoot = string.IsNullOrWhiteSpace(configuredDownload)
            ? Path.Combine(dataDirectory, "downloads")
            : configuredDownload;
        string? configuredUploads = Environment.GetEnvironmentVariable("MAILKIT_AGENT_UPLOAD_ROOTS");
        IReadOnlyList<string> uploadRoots = string.IsNullOrWhiteSpace(configuredUploads)
            ? Array.Empty<string>()
            : configuredUploads
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .ToArray();

        return new MailFileOptions(Path.GetFullPath(downloadRoot), uploadRoots);
    }
}
