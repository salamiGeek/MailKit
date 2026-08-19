using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Attachments;

namespace MailKit.Agent.Mail.Tests.Attachments;

public sealed class AttachmentServiceTests
{
    private string testDirectory = null!;
    private string downloadRoot = null!;

    [SetUp]
    public void SetUp()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "MailKit.Agent.Tests", Guid.NewGuid().ToString("N"));
        downloadRoot = Path.Combine(testDirectory, "downloads");
        Directory.CreateDirectory(downloadRoot);
    }

    [TearDown]
    public void TearDown()
    {
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

    private AttachmentService CreateService(long maxBytes) => CreateService(downloadRoot, maxBytes);

    private AttachmentService CreateService(long maxAttachmentBytes, long maxDownloadBytesPerCall) =>
        new(new MailFileOptions(downloadRoot, Array.Empty<string>()),
            new MailSafetyLimits(4096, maxAttachmentBytes, maxDownloadBytesPerCall, 100));

    private static AttachmentService CreateService(string root, long maxBytes) =>
        new(new MailFileOptions(root, Array.Empty<string>()),
            new MailSafetyLimits(4096, maxBytes, maxBytes, 100));

    private static AttachmentDescriptor Descriptor(string fileName) =>
        new("part-7", fileName, "application/octet-stream", 4, false, null);
}
