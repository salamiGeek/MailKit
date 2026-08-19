using System.ComponentModel;
using System.Runtime.InteropServices;
using MailKit.Agent.Core.Errors;
using Microsoft.Win32.SafeHandles;

namespace MailKit.Agent.Mail.Attachments;

public sealed class UploadAttachmentPathPolicy : IDisposable
{
    private const string NotAllowedCode = "attachment.upload_path_not_allowed";
    private readonly IReadOnlyList<ApprovedRoot> roots;
    private readonly StringComparison pathComparison;

    public UploadAttachmentPathPolicy(IReadOnlyList<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var approvedRoots = new List<ApprovedRoot>();
        try
        {
            foreach (string root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                new AttachmentPathPolicy(fullRoot)
                    .EnsureExistingComponentsAreNotReparsePoints(fullRoot);
                if (!Directory.Exists(fullRoot))
                    throw new DirectoryNotFoundException(fullRoot);

                approvedRoots.Add(ApprovedRoot.Open(fullRoot));
            }

            this.roots = approvedRoots;
        }
        catch (Exception exception) when (IsPathFailure(exception))
        {
            foreach (ApprovedRoot approvedRoot in approvedRoots)
                approvedRoot.Dispose();

            throw CreateNotAllowedError();
        }
    }

    public Stream OpenRead(string requestedPath)
    {
        if (roots.Count == 0)
        {
            throw AttachmentPathPolicy.CreatePolicyError(
                "attachment.upload_roots_required",
                "SMTP attachments require an explicitly configured upload root.");
        }

        if (string.IsNullOrWhiteSpace(requestedPath) || !Path.IsPathRooted(requestedPath))
            throw CreateNotAllowedError();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw CreateNotAllowedError();
        }

        foreach (ApprovedRoot root in roots)
        {
            string rootWithSeparator = Path.EndsInDirectorySeparator(root.Path)
                ? root.Path
                : root.Path + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, pathComparison))
                continue;

            try
            {
                return root.OpenRead(fullPath);
            }
            catch (Exception exception) when (IsPathFailure(exception))
            {
                throw CreateNotAllowedError();
            }
        }

        throw CreateNotAllowedError();
    }

    public void Dispose()
    {
        foreach (ApprovedRoot root in roots)
            root.Dispose();
    }

    private static bool IsPathFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or Win32Exception or
            PlatformNotSupportedException or MailOperationException;

    private static MailOperationException CreateNotAllowedError() =>
        AttachmentPathPolicy.CreatePolicyError(
            NotAllowedCode,
            "The SMTP attachment path is outside the configured upload roots or cannot be opened safely.");

    private sealed class ApprovedRoot : IDisposable
    {
        private readonly WindowsRootBinding? windows;
        private readonly LinuxRootBinding? linux;

        private ApprovedRoot(
            string path,
            WindowsRootBinding? windows,
            LinuxRootBinding? linux)
        {
            Path = path;
            this.windows = windows;
            this.linux = linux;
        }

        public string Path { get; }

        public static ApprovedRoot Open(string path)
        {
            if (OperatingSystem.IsWindows())
                return new ApprovedRoot(path, WindowsRootBinding.Open(path), null);
            if (OperatingSystem.IsLinux())
                return new ApprovedRoot(path, null, LinuxRootBinding.Open(path));

            throw new PlatformNotSupportedException(
                "Identity-safe attachment reads are not available on this platform.");
        }

        public Stream OpenRead(string fullPath)
        {
            if (windows is not null)
                return windows.OpenRead(fullPath);
            if (linux is not null)
                return linux.OpenRead(fullPath);

            throw new PlatformNotSupportedException();
        }

        public void Dispose()
        {
            windows?.Dispose();
            linux?.Dispose();
        }
    }

    private sealed class WindowsRootBinding : IDisposable
    {
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint GenericRead = 0x80000000;
        private const uint OpenExisting = 3;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;

        private readonly string root;
        private readonly FileIdentity identity;

        private WindowsRootBinding(string root, FileIdentity identity)
        {
            this.root = root;
            this.identity = identity;
        }

        public static WindowsRootBinding Open(string root)
        {
            IReadOnlyList<SafeFileHandle> handles = PinDirectoryChain(root);
            try
            {
                return new WindowsRootBinding(root, GetIdentity(handles[^1]));
            }
            finally
            {
                DisposeHandles(handles);
            }
        }

        public Stream OpenRead(string fullPath)
        {
            string? parent = System.IO.Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(parent))
                throw new IOException("The upload path has no parent directory.");

            IReadOnlyList<SafeFileHandle> rootPins = PinDirectoryChain(root);
            IReadOnlyList<SafeFileHandle>? parentPins = null;
            try
            {
                if (GetIdentity(rootPins[^1]) != identity)
                    throw new IOException("The configured upload root changed identity.");

                parentPins = PinDirectoryChain(parent);
                SafeFileHandle file = CreateFile(
                    fullPath,
                    GenericRead,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint,
                    IntPtr.Zero);
                if (file.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                try
                {
                    EnsureNotReparsePoint(file);
                    return new FileStream(file, FileAccess.Read);
                }
                catch
                {
                    file.Dispose();
                    throw;
                }
            }
            finally
            {
                if (parentPins is not null)
                    DisposeHandles(parentPins);
                DisposeHandles(rootPins);
            }
        }

        public void Dispose()
        {
        }

        private static IReadOnlyList<SafeFileHandle> PinDirectoryChain(string path)
        {
            string? pathRoot = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(pathRoot))
                throw new IOException("The upload path is not rooted.");

            var handles = new List<SafeFileHandle>();
            try
            {
                string current = pathRoot;
                handles.Add(OpenDirectory(current));
                string relative = System.IO.Path.GetRelativePath(pathRoot, path);
                foreach (string component in relative.Split(
                    new[] { System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = System.IO.Path.Combine(current, component);
                    handles.Add(OpenDirectory(current));
                }

                return handles;
            }
            catch
            {
                DisposeHandles(handles);
                throw;
            }
        }

        private static SafeFileHandle OpenDirectory(string path)
        {
            SafeFileHandle handle = CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                EnsureNotReparsePoint(handle);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        private static void EnsureNotReparsePoint(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new IOException("The upload path contains a reparse point.");
        }

        private static FileIdentity GetIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return new FileIdentity(
                information.VolumeSerialNumber,
                information.FileIndexHigh,
                information.FileIndexLow);
        }

        private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
        {
            foreach (SafeFileHandle handle in handles)
                handle.Dispose();
        }

        private readonly record struct FileIdentity(
            uint VolumeSerialNumber,
            uint FileIndexHigh,
            uint FileIndexLow);

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out ByHandleFileInformation fileInformation);
    }

    private sealed class LinuxRootBinding : IDisposable
    {
        private const int OpenCloseOnExec = 0x80000;
        private const int OpenDirectory = 0x10000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenReadOnly = 0;

        private readonly string root;
        private readonly SafeFileHandle rootHandle;

        private LinuxRootBinding(string root, SafeFileHandle rootHandle)
        {
            this.root = root;
            this.rootHandle = rootHandle;
        }

        public static LinuxRootBinding Open(string root) =>
            new(root, OpenDirectoryChain(root));

        public Stream OpenRead(string fullPath)
        {
            string relative = System.IO.Path.GetRelativePath(root, fullPath);
            string[] components = relative.Split(
                System.IO.Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0)
                throw new IOException("The upload path does not name a file.");

            int current = OpenAt(
                rootHandle.DangerousGetHandle().ToInt32(),
                ".",
                OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                0);
            if (current < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            try
            {
                for (int index = 0; index < components.Length - 1; index++)
                {
                    int next = OpenAt(
                        current,
                        components[index],
                        OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                        0);
                    if (next < 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());

                    Close(current);
                    current = next;
                }

                int file = OpenAt(
                    current,
                    components[^1],
                    OpenReadOnly | OpenNoFollow | OpenCloseOnExec,
                    0);
                if (file < 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());

                return new FileStream(
                    new SafeFileHandle((IntPtr)file, ownsHandle: true),
                    FileAccess.Read);
            }
            finally
            {
                Close(current);
            }
        }

        public void Dispose() => rootHandle.Dispose();

        private static SafeFileHandle OpenDirectoryChain(string path)
        {
            int current = Open("/", OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
            if (current < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            try
            {
                foreach (string component in path.Split(
                    System.IO.Path.DirectorySeparatorChar,
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    int next = OpenAt(
                        current,
                        component,
                        OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                        0);
                    if (next < 0)
                        throw new Win32Exception(Marshal.GetLastPInvokeError());

                    Close(current);
                    current = next;
                }

                var result = new SafeFileHandle((IntPtr)current, ownsHandle: true);
                current = -1;
                return result;
            }
            finally
            {
                if (current >= 0)
                    Close(current);
            }
        }

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int directory, string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fileDescriptor);
    }
}
