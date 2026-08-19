namespace MailKit.Agent.Core.Storage;

public sealed record MailFileOptions(
    string DownloadRoot,
    IReadOnlyList<string> UploadRoots);
