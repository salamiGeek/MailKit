using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Attachments;
using Microsoft.Extensions.DependencyInjection;

namespace MailKit.Agent.Mcp.Tests.Storage;

[NonParallelizable]
public sealed class MailStorageRegistrationTests
{
    [Test]
    public void HostRegistersResolvedFileOptionsWriterAndUploadPolicy()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string downloadRoot = Path.Combine(dataDirectory, "custom-downloads");
        string uploadRoot = Path.Combine(dataDirectory, "uploads");
        string? originalDownload = Environment.GetEnvironmentVariable("MAILKIT_AGENT_DOWNLOAD_ROOT");
        string? originalUploads = Environment.GetEnvironmentVariable("MAILKIT_AGENT_UPLOAD_ROOTS");

        try
        {
            Directory.CreateDirectory(uploadRoot);
            Environment.SetEnvironmentVariable("MAILKIT_AGENT_DOWNLOAD_ROOT", downloadRoot);
            Environment.SetEnvironmentVariable("MAILKIT_AGENT_UPLOAD_ROOTS", uploadRoot);
            var services = new ServiceCollection();

            McpServerHost.ConfigureMailStorage(services, dataDirectory);
            using ServiceProvider provider = services.BuildServiceProvider();

            MailFileOptions options = provider.GetRequiredService<MailFileOptions>();
            Assert.Multiple(() =>
            {
                Assert.That(options.DownloadRoot, Is.EqualTo(Path.GetFullPath(downloadRoot)));
                Assert.That(options.UploadRoots, Is.EqualTo(new[] { Path.GetFullPath(uploadRoot) }));
                Assert.That(provider.GetRequiredService<IAttachmentWriter>(),
                    Is.TypeOf<AttachmentService>());
                Assert.That(provider.GetRequiredService<UploadAttachmentPathPolicy>(), Is.Not.Null);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("MAILKIT_AGENT_DOWNLOAD_ROOT", originalDownload);
            Environment.SetEnvironmentVariable("MAILKIT_AGENT_UPLOAD_ROOTS", originalUploads);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
