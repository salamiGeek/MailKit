using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Attachments;

namespace MailKit.Agent.Mail.Tests.Attachments;

[NonParallelizable]
public sealed class AttachmentServiceTests
{
    private string testDirectory = null!;
    private string downloadRoot = null!;
    private List<IDisposable> disposables = null!;

    [SetUp]
    public void SetUp()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "MailKit.Agent.Tests", Guid.NewGuid().ToString("N"));
        downloadRoot = Path.Combine(testDirectory, "downloads");
        disposables = new List<IDisposable>();
        Directory.CreateDirectory(downloadRoot);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (IDisposable disposable in disposables)
            disposable.Dispose();

        if (Directory.Exists(testDirectory))
            Directory.Delete(testDirectory, recursive: true);
    }

    [TestCase("..")]
    [TestCase("../escape.bin")]
    [TestCase("subdirectory/escape.bin")]
    public void RejectsTraversalAndDirectoryComponents(string destinationName)
    {
        var service = CreateService(maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("source.bin"), destinationName, CancellationToken.None));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.path_outside_root"));
    }

    [Test]
    public void RejectsRootedDestinationName()
    {
        var service = CreateService(maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1 });
        string rootedName = Path.Combine(Path.GetPathRoot(downloadRoot)!, "escape.bin");

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("source.bin"), rootedName, CancellationToken.None));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.path_outside_root"));
    }

    [TestCase("")]
    [TestCase("CON")]
    [TestCase("nul.txt")]
    [TestCase("trailing.")]
    public void RejectsEmptyReservedOrAmbiguousNames(string destinationName)
    {
        var service = CreateService(maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("source.bin"), destinationName, CancellationToken.None));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.invalid_name"));
    }

    [Test]
    public void RejectsDescriptorFilenameTraversalWhenNoDestinationOverrideIsProvided()
    {
        var service = CreateService(maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("../../escape.bin"), null, CancellationToken.None));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.path_outside_root"));
    }

    [Test]
    public void RejectsDownloadRootThatIsAReparsePoint()
    {
        string outside = Path.Combine(testDirectory, "outside");
        string linkedRoot = Path.Combine(testDirectory, "linked-downloads");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linkedRoot, outside);

        var exception = Assert.Throws<MailOperationException>(() =>
            CreateService(linkedRoot, maxBytes: 1024));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.path_reparse_point"));
    }

    [Test]
    public void RejectsOversizeStreamAndDeletesOnlyItsOwnTemporaryFile()
    {
        string unrelatedTemp = Path.Combine(downloadRoot, "do-not-delete.tmp");
        File.WriteAllText(unrelatedTemp, "sentinel");
        var service = CreateService(maxBytes: 3);
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("large.bin"), null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Error.Code, Is.EqualTo("attachment.too_large"));
            Assert.That(File.ReadAllText(unrelatedTemp), Is.EqualTo("sentinel"));
            Assert.That(File.Exists(Path.Combine(downloadRoot, "large.bin")), Is.False);
            Assert.That(Directory.GetFiles(downloadRoot, "*.tmp"),
                Is.EqualTo(new[] { unrelatedTemp }));
        });
    }

    [Test]
    public void RejectsStreamThatExceedsPerCallDownloadLimit()
    {
        var service = CreateService(maxAttachmentBytes: 10, maxDownloadBytesPerCall: 3);
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("large.bin"), null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Error.Code, Is.EqualTo("attachment.too_large"));
            Assert.That(File.Exists(Path.Combine(downloadRoot, "large.bin")), Is.False);
            Assert.That(Directory.GetFiles(downloadRoot, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    public async Task SavesAtomicallyWithoutLeavingTemporaryFiles()
    {
        var service = CreateService(maxBytes: 1024);
        byte[] bytes = { 1, 2, 3, 4 };
        using var source = new MemoryStream(bytes);

        AttachmentSaveResult result = await service.SaveAsync(
            source, Descriptor("original.bin"), "saved.bin", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.AttachmentId, Is.EqualTo("part-7"));
            Assert.That(result.FileName, Is.EqualTo("saved.bin"));
            Assert.That(result.BytesWritten, Is.EqualTo(bytes.Length));
            Assert.That(result.Path, Is.EqualTo(Path.Combine(downloadRoot, "saved.bin")));
            Assert.That(File.ReadAllBytes(result.Path), Is.EqualTo(bytes));
            Assert.That(Directory.GetFiles(downloadRoot),
                Is.EqualTo(new[] { Path.Combine(downloadRoot, "saved.bin") }));
        });
    }

    [Test]
    public void DoesNotOverwriteAnExistingDestination()
    {
        string destination = Path.Combine(downloadRoot, "existing.bin");
        File.WriteAllBytes(destination, new byte[] { 9 });
        var service = CreateService(maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("existing.bin"), null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Error.Code, Is.EqualTo("attachment.destination_exists"));
            Assert.That(File.ReadAllBytes(destination), Is.EqualTo(new byte[] { 9 }));
            Assert.That(Directory.GetFiles(downloadRoot, "*.tmp"), Is.Empty);
        });
    }

    [Test]
    [Platform("Win")]
    public async Task PinnedRootPreventsSwapBeforeIdentityBasedCommit()
    {
        string movedRoot = Path.Combine(testDirectory, "moved-downloads");
        string outside = Path.Combine(testDirectory, "outside");
        Directory.CreateDirectory(outside);
        var inner = new AtomicAttachmentFileFactory(new AttachmentPathPolicy(downloadRoot));
        var swapping = new RootSwappingFileFactory(inner, downloadRoot, movedRoot, outside);
        var service = CreateService(swapping, maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });

        AttachmentSaveResult result = await service.SaveAsync(
            source, Descriptor("saved.bin"), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(swapping.SwapException, Is.TypeOf<IOException>());
            Assert.That(result.Path, Is.EqualTo(Path.Combine(downloadRoot, "saved.bin")));
            Assert.That(File.ReadAllBytes(result.Path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(File.Exists(Path.Combine(outside, "saved.bin")), Is.False);
            Assert.That(Directory.Exists(movedRoot), Is.False);
        });
    }

    [Test]
    [Platform("Win")]
    public void ReplacedRootBeforeTransactionIsRejectedByConfiguredIdentity()
    {
        string originalRoot = Path.Combine(testDirectory, "original-downloads");
        var inner = new AtomicAttachmentFileFactory(new AttachmentPathPolicy(downloadRoot));
        var service = CreateService(inner, maxBytes: 1024);
        Directory.Move(downloadRoot, originalRoot);
        Directory.CreateDirectory(downloadRoot);
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await service.SaveAsync(source, Descriptor("saved.bin"), null, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Error.Code, Is.EqualTo("attachment.path_identity_changed"));
            Assert.That(File.Exists(Path.Combine(downloadRoot, "saved.bin")), Is.False);
            Assert.That(File.Exists(Path.Combine(originalRoot, "saved.bin")), Is.False);
        });
    }

    [Test]
    [Platform("Win")]
    public async Task CleanupByOpenFileIdentityDoesNotDeleteReplacementAtTemporaryPath()
    {
        var inner = new AtomicAttachmentFileFactory(new AttachmentPathPolicy(downloadRoot));
        var replacing = new ReplacementAfterDisposeFileFactory(inner, "replacement");
        var service = CreateService(replacing, maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });

        AttachmentSaveResult result = await service.SaveAsync(
            source, Descriptor("saved.bin"), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(result.Path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(replacing.ReplacementPath, Is.Not.Null);
            Assert.That(File.ReadAllText(replacing.ReplacementPath!), Is.EqualTo("replacement"));
        });
    }

    [Test]
    public void ResolvesDownloadAndUploadRootsOnlyFromSpecifiedEnvironmentVariables()
    {
        string configuredDownload = Path.Combine(testDirectory, "configured-downloads");
        string firstUpload = Path.Combine(testDirectory, "upload-one");
        string secondUpload = Path.Combine(testDirectory, "upload-two");

        WithMailFileEnvironment(
            configuredDownload,
            $"{firstUpload}{Path.PathSeparator}{secondUpload}",
            () =>
            {
                MailFileOptions options = MailFileOptionsResolver.Resolve(testDirectory);
                Assert.Multiple(() =>
                {
                    Assert.That(options.DownloadRoot, Is.EqualTo(Path.GetFullPath(configuredDownload)));
                    Assert.That(options.UploadRoots, Is.EqualTo(new[]
                    {
                        Path.GetFullPath(firstUpload),
                        Path.GetFullPath(secondUpload)
                    }));
                });
            });
    }

    [Test]
    public void DefaultsDownloadRootUnderDataAndLeavesUploadRootsEmpty()
    {
        WithMailFileEnvironment(null, null, () =>
        {
            MailFileOptions options = MailFileOptionsResolver.Resolve(testDirectory);

            Assert.Multiple(() =>
            {
                Assert.That(options.DownloadRoot,
                    Is.EqualTo(Path.GetFullPath(Path.Combine(testDirectory, "downloads"))));
                Assert.That(options.UploadRoots, Is.Empty);
            });
        });
    }

    [Test]
    public void EmptyUploadRootsRejectAttachmentReads()
    {
        using var policy = new UploadAttachmentPathPolicy(Array.Empty<string>());

        var exception = Assert.Throws<MailOperationException>(() =>
            policy.OpenRead(Path.Combine(testDirectory, "attachment.bin")));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.upload_roots_required"));
    }

    [Test]
    public void UploadOpenRejectsSymlinkComponentEscapingConfiguredRoot()
    {
        string uploadRoot = Path.Combine(testDirectory, "uploads");
        string outside = Path.Combine(testDirectory, "outside-upload");
        string link = Path.Combine(uploadRoot, "linked");
        Directory.CreateDirectory(uploadRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.bin"), "outside");
        Directory.CreateSymbolicLink(link, outside);
        using var policy = new UploadAttachmentPathPolicy(new[] { uploadRoot });

        var exception = Assert.Throws<MailOperationException>(() =>
            policy.OpenRead(Path.Combine(link, "secret.bin")));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.upload_path_not_allowed"));
    }

    [Test]
    public void UploadPolicyRejectsConfiguredRootThatIsAReparsePoint()
    {
        string outside = Path.Combine(testDirectory, "outside-root");
        string linkedRoot = Path.Combine(testDirectory, "linked-upload-root");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(linkedRoot, outside);

        var exception = Assert.Throws<MailOperationException>(() =>
            new UploadAttachmentPathPolicy(new[] { linkedRoot }));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.upload_path_not_allowed"));
    }

    [Test]
    [Platform("Win,Linux")]
    public void UploadOpenReturnsStreamBoundToApprovedFileIdentity()
    {
        string uploadRoot = Path.Combine(testDirectory, "uploads");
        string attachment = Path.Combine(uploadRoot, "attachment.bin");
        Directory.CreateDirectory(uploadRoot);
        File.WriteAllText(attachment, "approved");
        using var policy = new UploadAttachmentPathPolicy(new[] { uploadRoot });

        using Stream stream = policy.OpenRead(attachment);
        using var reader = new StreamReader(stream);

        Assert.That(reader.ReadToEnd(), Is.EqualTo("approved"));
    }

    [Test]
    [Platform("Win")]
    public void UploadOpenRejectsConfiguredRootIdentityReplacement()
    {
        string uploadRoot = Path.Combine(testDirectory, "uploads");
        string originalRoot = Path.Combine(testDirectory, "original-uploads");
        Directory.CreateDirectory(uploadRoot);
        using var policy = new UploadAttachmentPathPolicy(new[] { uploadRoot });
        Directory.Move(uploadRoot, originalRoot);
        Directory.CreateDirectory(uploadRoot);
        string replacement = Path.Combine(uploadRoot, "replacement.bin");
        File.WriteAllText(replacement, "replacement");

        var exception = Assert.Throws<MailOperationException>(() => policy.OpenRead(replacement));

        Assert.That(exception!.Error.Code, Is.EqualTo("attachment.upload_path_not_allowed"));
    }

    [Test]
    public void LinuxPublicationUsesProcDescriptorLinkWithoutElevatedCapability()
    {
        var api = new RecordingLinuxLinkApi();
        var publisher = new LinuxUnprivilegedFilePublisher(api);

        publisher.Publish(openFileDescriptor: 41, rootDirectoryDescriptor: 42, "saved.bin");

        Assert.That(api.Call, Is.EqualTo(new LinuxLinkCall(
            -100, "/proc/self/fd/41", 42, "saved.bin", 0x400)));
    }

    [Test]
    [Platform("Linux")]
    public async Task LinuxUnprivilegedSavePublishesIdentityBoundFile()
    {
        var service = CreateService(maxBytes: 1024);
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });

        AttachmentSaveResult result = await service.SaveAsync(
            source, Descriptor("linux.bin"), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllBytes(result.Path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(Directory.GetFiles(downloadRoot, "*.tmp"), Is.Empty);
        });
    }

    private AttachmentService CreateService(long maxBytes) => CreateService(downloadRoot, maxBytes);

    private AttachmentService CreateService(IAtomicAttachmentFileFactory fileFactory, long maxBytes) =>
        Track(new AttachmentService(
            new MailFileOptions(downloadRoot, Array.Empty<string>()),
            new MailSafetyLimits(4096, maxBytes, maxBytes, 100), fileFactory));

    private AttachmentService CreateService(long maxAttachmentBytes, long maxDownloadBytesPerCall) =>
        Track(new AttachmentService(
            new MailFileOptions(downloadRoot, Array.Empty<string>()),
            new MailSafetyLimits(4096, maxAttachmentBytes, maxDownloadBytesPerCall, 100)));

    private AttachmentService CreateService(string root, long maxBytes) =>
        Track(new AttachmentService(
            new MailFileOptions(root, Array.Empty<string>()),
            new MailSafetyLimits(4096, maxBytes, maxBytes, 100)));

    private AttachmentService Track(AttachmentService service)
    {
        disposables.Add(service);
        return service;
    }

    private static AttachmentDescriptor Descriptor(string fileName) =>
        new("part-7", fileName, "application/octet-stream", 4, false, null);

    private static void WithMailFileEnvironment(
        string? downloadRoot,
        string? uploadRoots,
        TestDelegate assertion)
    {
        const string downloadName = "MAILKIT_AGENT_DOWNLOAD_ROOT";
        const string uploadsName = "MAILKIT_AGENT_UPLOAD_ROOTS";
        string? originalDownload = Environment.GetEnvironmentVariable(downloadName);
        string? originalUploads = Environment.GetEnvironmentVariable(uploadsName);

        try
        {
            Environment.SetEnvironmentVariable(downloadName, downloadRoot);
            Environment.SetEnvironmentVariable(uploadsName, uploadRoots);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(downloadName, originalDownload);
            Environment.SetEnvironmentVariable(uploadsName, originalUploads);
        }
    }

    private sealed class RootSwappingFileFactory : IAtomicAttachmentFileFactory
    {
        private readonly IAtomicAttachmentFileFactory inner;
        private readonly string root;
        private readonly string movedRoot;
        private readonly string outside;

        public RootSwappingFileFactory(
            IAtomicAttachmentFileFactory inner,
            string root,
            string movedRoot,
            string outside)
        {
            this.inner = inner;
            this.root = root;
            this.movedRoot = movedRoot;
            this.outside = outside;
        }

        public Exception? SwapException { get; private set; }

        public IAtomicAttachmentFile Create(string destinationName)
        {
            IAtomicAttachmentFile file = inner.Create(destinationName);
            return new DelegatingAtomicAttachmentFile(file, beforeCommit: () =>
            {
                try
                {
                    Directory.Move(root, movedRoot);
                    Directory.CreateSymbolicLink(root, outside);
                }
                catch (Exception exception)
                {
                    SwapException = exception;
                }
            });
        }
    }

    private sealed class ReplacementAfterDisposeFileFactory : IAtomicAttachmentFileFactory
    {
        private readonly IAtomicAttachmentFileFactory inner;
        private readonly string replacement;

        public ReplacementAfterDisposeFileFactory(
            IAtomicAttachmentFileFactory inner,
            string replacement)
        {
            this.inner = inner;
            this.replacement = replacement;
        }

        public string? ReplacementPath { get; private set; }

        public IAtomicAttachmentFile Create(string destinationName)
        {
            IAtomicAttachmentFile file = inner.Create(destinationName);
            return new DelegatingAtomicAttachmentFile(file, afterDispose: () =>
            {
                string temporaryPath = file.TemporaryPath ??
                    throw new InvalidOperationException("The Windows transaction must expose its temporary path.");
                ReplacementPath = temporaryPath;
                File.WriteAllText(temporaryPath, replacement);
            });
        }
    }

    private sealed class DelegatingAtomicAttachmentFile : IAtomicAttachmentFile
    {
        private readonly IAtomicAttachmentFile inner;
        private readonly Action? beforeCommit;
        private readonly Action? afterDispose;

        public DelegatingAtomicAttachmentFile(
            IAtomicAttachmentFile inner,
            Action? beforeCommit = null,
            Action? afterDispose = null)
        {
            this.inner = inner;
            this.beforeCommit = beforeCommit;
            this.afterDispose = afterDispose;
        }

        public Stream Stream => inner.Stream;
        public string DestinationPath => inner.DestinationPath;
        public string? TemporaryPath => inner.TemporaryPath;

        public void Commit()
        {
            beforeCommit?.Invoke();
            inner.Commit();
        }

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            afterDispose?.Invoke();
        }
    }

    private sealed class RecordingLinuxLinkApi : ILinuxLinkApi
    {
        public LinuxLinkCall? Call { get; private set; }

        public int LinkAt(
            int oldDirectory,
            string oldPath,
            int newDirectory,
            string newPath,
            int flags)
        {
            Call = new LinuxLinkCall(oldDirectory, oldPath, newDirectory, newPath, flags);
            return 0;
        }

        public int GetLastError() => 0;
    }

    private sealed record LinuxLinkCall(
        int OldDirectory,
        string OldPath,
        int NewDirectory,
        string NewPath,
        int Flags);
}
