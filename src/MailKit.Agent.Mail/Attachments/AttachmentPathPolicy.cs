using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Mail.Attachments;

public sealed class AttachmentPathPolicy
{
    private static readonly HashSet<string> WindowsReservedNames = CreateWindowsReservedNames();
    private readonly string rootWithSeparator;
    private readonly StringComparison pathComparison;

    public AttachmentPathPolicy(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        rootWithSeparator = Path.EndsInDirectorySeparator(Root)
            ? Root
            : Root + Path.DirectorySeparatorChar;
        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    public string Root { get; }

    public string ResolveDestination(string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
            throw CreatePolicyError("attachment.invalid_name", "The attachment destination name is invalid.");

        if (Path.IsPathRooted(requestedName) || requestedName is "." or ".." ||
            requestedName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            requestedName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
        {
            throw CreatePolicyError("attachment.path_outside_root", "The attachment destination must be inside the download root.");
        }

        string safeName = Path.GetFileName(requestedName);
        if (!string.Equals(safeName, requestedName, StringComparison.Ordinal) || !IsValidFileName(safeName))
            throw CreatePolicyError("attachment.invalid_name", "The attachment destination name is invalid.");

        string destination = Path.GetFullPath(Path.Combine(Root, safeName));
        if (!destination.StartsWith(rootWithSeparator, pathComparison))
            throw CreatePolicyError("attachment.path_outside_root", "The attachment destination must be inside the download root.");

        return destination;
    }

    public void EnsureExistingComponentsAreNotReparsePoints(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(pathRoot))
            throw CreatePolicyError("attachment.path_outside_root", "The attachment destination must be inside the download root.");

        string current = pathRoot;
        string relative = Path.GetRelativePath(pathRoot, fullPath);
        foreach (string component in relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (IOException)
            {
                throw CreatePolicyError("attachment.path_reparse_point", "The attachment path could not be safely resolved.");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw CreatePolicyError("attachment.path_reparse_point", "The attachment path cannot contain a reparse point.");
        }
    }

    private static bool IsValidFileName(string fileName)
    {
        if (fileName.Length == 0 || fileName.EndsWith(' ') || fileName.EndsWith('.') ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        string baseName = fileName.Split('.')[0];
        return !WindowsReservedNames.Contains(baseName);
    }

    private static HashSet<string> CreateWindowsReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL"
        };

        for (int index = 1; index <= 9; index++)
        {
            names.Add($"COM{index}");
            names.Add($"LPT{index}");
        }

        return names;
    }

    internal static MailOperationException CreatePolicyError(string code, string message) =>
        new(new ToolError(code, ErrorCategory.Policy, message, false, null, null));
}
