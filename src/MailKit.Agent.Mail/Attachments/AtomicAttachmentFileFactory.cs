using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MailKit.Agent.Mail.Attachments;

internal interface IAtomicAttachmentFileFactory
{
    IAtomicAttachmentFile Create(string destinationName);
}

internal interface IAtomicAttachmentFile : IAsyncDisposable
{
    Stream Stream { get; }
    string DestinationPath { get; }
    string? TemporaryPath { get; }
    void Commit();
}

internal sealed class AtomicAttachmentFileFactory : IAtomicAttachmentFileFactory, IDisposable
{
    private readonly AttachmentPathPolicy pathPolicy;
    private readonly WindowsRootIdentity? windowsRootIdentity;
    private readonly SafeFileHandle? linuxRootHandle;

    public AtomicAttachmentFileFactory(AttachmentPathPolicy pathPolicy)
    {
        this.pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        if (OperatingSystem.IsWindows())
            windowsRootIdentity = WindowsAtomicAttachmentFile.CaptureRootIdentity(pathPolicy.Root);
        else if (OperatingSystem.IsLinux())
            linuxRootHandle = LinuxAtomicAttachmentFile.OpenConfiguredRoot(pathPolicy.Root);
    }

    public IAtomicAttachmentFile Create(string destinationName)
    {
        string destinationPath = pathPolicy.ResolveDestination(destinationName);

        if (OperatingSystem.IsWindows())
            return WindowsAtomicAttachmentFile.Create(
                pathPolicy, destinationPath, windowsRootIdentity!.Value);
        if (OperatingSystem.IsLinux())
            return LinuxAtomicAttachmentFile.Create(
                linuxRootHandle!, destinationPath);

        throw new PlatformNotSupportedException(
            "Identity-safe attachment saving is not available on this platform.");
    }

    public void Dispose() => linuxRootHandle?.Dispose();

    private readonly record struct WindowsRootIdentity(
        uint VolumeSerialNumber,
        uint FileIndexHigh,
        uint FileIndexLow);

    private sealed class WindowsAtomicAttachmentFile : IAtomicAttachmentFile
    {
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint OpenExisting = 3;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorFileExists = 80;

        private readonly IReadOnlyList<SafeFileHandle> pinnedDirectories;
        private readonly FileStream stream;
        private bool committed;

        private WindowsAtomicAttachmentFile(
            string destinationPath,
            string temporaryPath,
            IReadOnlyList<SafeFileHandle> pinnedDirectories,
            FileStream stream)
        {
            DestinationPath = destinationPath;
            TemporaryPath = temporaryPath;
            this.pinnedDirectories = pinnedDirectories;
            this.stream = stream;
        }

        public Stream Stream => stream;
        public string DestinationPath { get; }
        public string? TemporaryPath { get; }

        public static WindowsAtomicAttachmentFile Create(
            AttachmentPathPolicy pathPolicy,
            string destinationPath,
            WindowsRootIdentity expectedRootIdentity)
        {
            IReadOnlyList<SafeFileHandle> pinnedDirectories = PinDirectoryChain(pathPolicy.Root);
            WindowsRootIdentity actualRootIdentity = GetIdentity(pinnedDirectories[^1]);
            if (actualRootIdentity != expectedRootIdentity)
            {
                DisposeHandles(pinnedDirectories);
                throw AttachmentPathPolicy.CreatePolicyError(
                    "attachment.path_identity_changed",
                    "The configured attachment root changed identity.");
            }

            string temporaryPath = Path.Combine(pathPolicy.Root, $"{Guid.NewGuid():N}.tmp");

            try
            {
                var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.DeleteOnClose);
                return new WindowsAtomicAttachmentFile(
                    destinationPath, temporaryPath, pinnedDirectories, stream);
            }
            catch
            {
                DisposeHandles(pinnedDirectories);
                throw;
            }
        }

        public static WindowsRootIdentity CaptureRootIdentity(string root)
        {
            IReadOnlyList<SafeFileHandle> handles = PinDirectoryChain(root);
            try
            {
                return GetIdentity(handles[^1]);
            }
            finally
            {
                DisposeHandles(handles);
            }
        }

        public void Commit()
        {
            if (committed)
                throw new InvalidOperationException("The attachment file is already committed.");

            stream.Flush(flushToDisk: true);
            if (!CreateHardLink(DestinationPath, TemporaryPath!, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is ErrorAlreadyExists or ErrorFileExists)
                {
                    throw AttachmentPathPolicy.CreatePolicyError(
                        "attachment.destination_exists",
                        "The attachment destination already exists.");
                }

                throw new Win32Exception(error);
            }

            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                DisposeHandles(pinnedDirectories);
            }
        }

        private static IReadOnlyList<SafeFileHandle> PinDirectoryChain(string root)
        {
            string? pathRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(pathRoot))
                throw new IOException("The attachment root is not rooted.");

            var handles = new List<SafeFileHandle>();
            try
            {
                string current = pathRoot;
                handles.Add(OpenDirectoryWithoutDeleteSharing(current));
                string relative = Path.GetRelativePath(pathRoot, root);
                foreach (string component in relative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);
                    handles.Add(OpenDirectoryWithoutDeleteSharing(current));
                }

                return handles;
            }
            catch
            {
                DisposeHandles(handles);
                throw;
            }
        }

        private static SafeFileHandle OpenDirectoryWithoutDeleteSharing(string path)
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

            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                handle.Dispose();
                throw AttachmentPathPolicy.CreatePolicyError(
                    "attachment.path_reparse_point",
                    "The attachment path cannot contain a reparse point.");
            }

            return handle;
        }

        private static WindowsRootIdentity GetIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return new WindowsRootIdentity(
                information.VolumeSerialNumber,
                information.FileIndexHigh,
                information.FileIndexLow);
        }

        private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
        {
            foreach (SafeFileHandle handle in handles)
                handle.Dispose();
        }

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }

    private sealed class LinuxAtomicAttachmentFile : IAtomicAttachmentFile
    {
        private const int AtEmptyPath = 0x1000;
        private const int ErrorAlreadyExists = 17;
        private const int OpenCloseOnExec = 0x80000;
        private const int OpenDirectory = 0x10000;
        private const int OpenNoFollow = 0x20000;
        private const int OpenReadOnly = 0;
        private const int OpenReadWrite = 2;
        private const int OpenTemporaryFile = 0x410000;
        private const uint OwnerReadWrite = 0x180;

        private readonly SafeFileHandle configuredRootHandle;
        private readonly FileStream stream;
        private bool committed;

        private LinuxAtomicAttachmentFile(
            string destinationPath,
            SafeFileHandle configuredRootHandle,
            FileStream stream)
        {
            DestinationPath = destinationPath;
            this.configuredRootHandle = configuredRootHandle;
            this.stream = stream;
        }

        public Stream Stream => stream;
        public string DestinationPath { get; }
        public string? TemporaryPath => null;

        public static LinuxAtomicAttachmentFile Create(
            SafeFileHandle configuredRootHandle,
            string destinationPath)
        {
            int fileDescriptor = OpenAt(
                configuredRootHandle.DangerousGetHandle().ToInt32(),
                ".",
                OpenTemporaryFile | OpenReadWrite | OpenCloseOnExec,
                OwnerReadWrite);
            if (fileDescriptor < 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new Win32Exception(error);
            }

            var fileHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
            var stream = new FileStream(fileHandle, FileAccess.Write, 81920, isAsync: true);
            return new LinuxAtomicAttachmentFile(destinationPath, configuredRootHandle, stream);
        }

        public void Commit()
        {
            if (committed)
                throw new InvalidOperationException("The attachment file is already committed.");

            stream.Flush(flushToDisk: true);
            int result = LinkAt(
                stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
                string.Empty,
                configuredRootHandle.DangerousGetHandle().ToInt32(),
                Path.GetFileName(DestinationPath),
                AtEmptyPath);
            if (result != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == ErrorAlreadyExists)
                {
                    throw AttachmentPathPolicy.CreatePolicyError(
                        "attachment.destination_exists",
                        "The attachment destination already exists.");
                }

                throw new Win32Exception(error);
            }

            committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        public static SafeFileHandle OpenConfiguredRoot(string root)
        {
            int current = Open("/", OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec, 0);
            if (current < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());

            try
            {
                foreach (string component in root.Split(
                    Path.DirectorySeparatorChar,
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

        [DllImport("libc", EntryPoint = "linkat", SetLastError = true)]
        private static extern int LinkAt(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath,
            int flags);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fileDescriptor);
    }
}
