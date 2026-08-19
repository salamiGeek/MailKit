using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Storage;

namespace MailKit.Agent.Mail.Attachments;

public sealed class AttachmentService : IAttachmentWriter
{
    private const int BufferSize = 81920;
    private readonly AttachmentPathPolicy pathPolicy;
    private readonly long maxAttachmentBytes;

    public AttachmentService(MailFileOptions fileOptions, MailSafetyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(fileOptions);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxAttachmentBytes <= 0 || limits.MaxDownloadBytesPerCall <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));

        pathPolicy = new AttachmentPathPolicy(fileOptions.DownloadRoot);
        maxAttachmentBytes = Math.Min(limits.MaxAttachmentBytes, limits.MaxDownloadBytesPerCall);

        pathPolicy.EnsureExistingComponentsAreNotReparsePoints(pathPolicy.Root);
        Directory.CreateDirectory(pathPolicy.Root);
        pathPolicy.EnsureExistingComponentsAreNotReparsePoints(pathPolicy.Root);
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
        string destination = pathPolicy.ResolveDestination(candidateName);
        pathPolicy.EnsureExistingComponentsAreNotReparsePoints(destination);

        string tempPath = Path.Combine(pathPolicy.Root, $"{Guid.NewGuid():N}.tmp");
        long bytesWritten = 0;

        try
        {
            await using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
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
                output.Flush(flushToDisk: true);
            }

            pathPolicy.EnsureExistingComponentsAreNotReparsePoints(destination);
            File.Move(tempPath, destination, overwrite: false);

            return new AttachmentSaveResult(
                descriptor.Id,
                Path.GetFileName(destination),
                destination,
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
        catch (IOException) when (File.Exists(destination))
        {
            throw AttachmentPathPolicy.CreatePolicyError(
                "attachment.destination_exists",
                "The attachment destination already exists.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new MailOperationException(new ToolError(
                "attachment.save_failed",
                ErrorCategory.Internal,
                "The attachment could not be saved.",
                false,
                null,
                null));
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
