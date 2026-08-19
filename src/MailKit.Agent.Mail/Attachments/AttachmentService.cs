using System.ComponentModel;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Storage;

namespace MailKit.Agent.Mail.Attachments;

public sealed class AttachmentService : IAttachmentWriter, IDisposable
{
    private const int BufferSize = 81920;
    private readonly IAtomicAttachmentFileFactory fileFactory;
    private readonly IDisposable? ownedFileFactory;
    private readonly long maxAttachmentBytes;

    public AttachmentService(MailFileOptions fileOptions, MailSafetyLimits limits)
        : this(fileOptions, limits, fileFactory: null)
    {
    }

    internal AttachmentService(
        MailFileOptions fileOptions,
        MailSafetyLimits limits,
        IAtomicAttachmentFileFactory? fileFactory)
    {
        ArgumentNullException.ThrowIfNull(fileOptions);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxAttachmentBytes <= 0 || limits.MaxDownloadBytesPerCall <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));

        var pathPolicy = new AttachmentPathPolicy(fileOptions.DownloadRoot);
        maxAttachmentBytes = Math.Min(limits.MaxAttachmentBytes, limits.MaxDownloadBytesPerCall);

        pathPolicy.EnsureExistingComponentsAreNotReparsePoints(pathPolicy.Root);
        Directory.CreateDirectory(pathPolicy.Root);
        pathPolicy.EnsureExistingComponentsAreNotReparsePoints(pathPolicy.Root);
        this.fileFactory = fileFactory ?? new AtomicAttachmentFileFactory(pathPolicy);
        ownedFileFactory = fileFactory is null ? (IDisposable)this.fileFactory : null;
    }

    public async Task<AttachmentSaveResult> SaveAsync(
        Stream source,
        AttachmentDescriptor descriptor,
        string? destinationName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!source.CanRead)
            throw new ArgumentException("The attachment stream must be readable.", nameof(source));

        string candidateName = destinationName ?? descriptor.FileName ?? string.Empty;
        long bytesWritten = 0;

        try
        {
            await using IAtomicAttachmentFile file = fileFactory.Create(candidateName);
            Stream output = file.Stream;
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                if (bytesWritten > maxAttachmentBytes - bytesRead)
                {
                    throw AttachmentPathPolicy.CreatePolicyError(
                        "attachment.too_large",
                        "The attachment exceeds the configured size limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
                bytesWritten += bytesRead;
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            file.Commit();

            return new AttachmentSaveResult(
                descriptor.Id,
                Path.GetFileName(file.DestinationPath),
                file.DestinationPath,
                bytesWritten);
        }
        catch (MailOperationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Win32Exception or PlatformNotSupportedException)
        {
            throw new MailOperationException(new ToolError(
                "attachment.save_failed",
                ErrorCategory.Internal,
                "The attachment could not be saved.",
                false,
                null,
                null));
        }
    }

    public void Dispose() => ownedFileFactory?.Dispose();
}
