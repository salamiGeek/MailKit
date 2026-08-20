using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// File-backed prepared-send store. One JSON file per preparation under
/// <c>&lt;data&gt;/send-preparations/&lt;preparation_id&gt;.json</c>, written through a
/// create-new temporary file and atomically moved over the destination (the same
/// crash-safety pattern as <see cref="JsonSendLedger"/>). Preparations therefore
/// survive a server process restart, which is required for stdio hosts that restart
/// the server between tool calls: <see cref="SendApplication.PrepareAsync"/> may run
/// in one process and <see cref="SendApplication.CommitAsync"/> in its successor.
/// <see cref="TakeAsync"/> opens the file with <c>FileShare.None</c> plus
/// <c>DeleteOnClose</c> so exactly one process can ever consume a preparation, while
/// <see cref="TryGetAsync"/> peeks without consuming. Expired preparations are swept
/// (deleted from disk) on every access; deleted files, not zeroed buffers, are this
/// store's cleanup contract because the MIME bytes were durable from the moment
/// <see cref="AddAsync"/> returned.
/// </summary>
public sealed class FilePreparedSendStore : IPreparedSendStore
{
    private static readonly Regex PreparationIdPattern =
        new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private readonly string storeDirectory;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    public FilePreparedSendStore(string dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        storeDirectory = Path.Combine(dataDirectory, "send-preparations");
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task AddAsync(PreparedOutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidatePreparationId(message.PreparationId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            SweepExpired();
            Write(message);
        }

        return Task.CompletedTask;
    }

    public Task<PreparedOutgoingMessage?> TryGetAsync(
        string preparationId, CancellationToken cancellationToken)
    {
        ValidatePreparationId(preparationId);
        cancellationToken.ThrowIfCancellationRequested();

        PreparedOutgoingMessage? peeked;
        lock (gate)
        {
            SweepExpired();
            peeked = Read(preparationId, consume: false);
        }

        return Task.FromResult(peeked);
    }

    public Task<PreparedOutgoingMessage?> TakeAsync(
        string preparationId, CancellationToken cancellationToken)
    {
        ValidatePreparationId(preparationId);
        cancellationToken.ThrowIfCancellationRequested();

        PreparedOutgoingMessage? taken;
        lock (gate)
        {
            SweepExpired();
            taken = Read(preparationId, consume: true);
        }

        return Task.FromResult(taken);
    }

    private void Write(PreparedOutgoingMessage message)
    {
        string path = PathOf(message.PreparationId);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(storeDirectory);

        try
        {
            using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, StoredMessage.Of(message));
                stream.Flush();
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private PreparedOutgoingMessage? Read(string preparationId, bool consume)
    {
        string path = PathOf(preparationId);
        if (!File.Exists(path))
            return null;

        PreparedOutgoingMessage? message;
        try
        {
            // Exclusive open (FileShare.None) serializes takers across processes;
            // DeleteOnClose makes the winner's consume durable even on a crash
            // between the read here and the handle release.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                consume ? FileShare.None : FileShare.Read,
                4096,
                FileOptions.Asynchronous | (consume ? FileOptions.DeleteOnClose : 0));
            var stored = JsonSerializer.Deserialize<StoredMessage>(stream);
            message = stored?.ToPreparedMessage();
        }
        catch (IOException)
        {
            // The file is locked by a concurrent consumer or vanished mid-read:
            // for a peek it is simply unavailable, for a take it is already gone.
            return null;
        }
        catch (JsonException)
        {
            // A corrupt record (for example a torn write) behaves as missing.
            TryDelete(path);
            return null;
        }

        if (message is null)
        {
            TryDelete(path);
            return null;
        }

        if (message.ExpiresAt <= timeProvider.GetUtcNow())
        {
            TryDelete(path);
            return null;
        }

        return message;
    }

    private void SweepExpired()
    {
        if (!Directory.Exists(storeDirectory))
            return;

        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (string path in Directory.EnumerateFiles(storeDirectory, "*.json"))
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                    FileOptions.Asynchronous);
                var stored = JsonSerializer.Deserialize<StoredMessage>(stream);
                if (stored is null || stored.ExpiresAt <= now)
                    TryDelete(path);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or UnauthorizedAccessException)
            {
                // Locked or unreadable records are left for a later sweep.
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A concurrent consumer holds the record; its own take decides.
        }
    }

    private string PathOf(string preparationId)
    {
        ValidatePreparationId(preparationId);
        return Path.Combine(storeDirectory, preparationId + ".json");
    }

    private static void ValidatePreparationId(string preparationId)
    {
        if (preparationId is null || !PreparationIdPattern.IsMatch(preparationId))
            throw new ArgumentException(
                "The preparation ID must be 32 lowercase hexadecimal characters.",
                nameof(preparationId));
    }

    /// <summary>
    /// On-disk shape of <see cref="PreparedOutgoingMessage"/>. A dedicated DTO (and
    /// the token-free <see cref="StoredPreview"/>) keeps the one-time confirmation
    /// token out of the persisted JSON: the token already lives only in the caller's
    /// hands, and a token at rest would be a committable capability for its whole
    /// TTL. The commit path re-derives everything from the caller-supplied token and
    /// this record's own fields, so nothing restores the token on read.
    /// </summary>
    private sealed record StoredMessage(
        [property: JsonPropertyName("preparation_id")] string PreparationId,
        [property: JsonPropertyName("account_id")] string AccountId,
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("content_hash")] string ContentHash,
        [property: JsonPropertyName("mime_message")] byte[] MimeMessage,
        [property: JsonPropertyName("envelope_sender")] string? EnvelopeSender,
        [property: JsonPropertyName("envelope_recipients")] IReadOnlyList<string> EnvelopeRecipients,
        [property: JsonPropertyName("preview")] StoredPreview Preview,
        [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
    {
        public static StoredMessage Of(PreparedOutgoingMessage message) => new(
            message.PreparationId,
            message.AccountId,
            message.MessageId,
            message.ContentHash,
            message.MimeMessage,
            message.EnvelopeSender,
            message.EnvelopeRecipients,
            StoredPreview.Of(message.Preview),
            message.IdempotencyKeyHash,
            message.ExpiresAt);

        public PreparedOutgoingMessage ToPreparedMessage() => new(
            PreparationId,
            AccountId,
            MessageId,
            ContentHash,
            MimeMessage,
            EnvelopeSender,
            EnvelopeRecipients,
            Preview.ToSendPreview(),
            IdempotencyKeyHash,
            ExpiresAt);
    }

    private sealed record StoredPreview(
        [property: JsonPropertyName("preparation_id")] string PreparationId,
        [property: JsonPropertyName("account_id")] string AccountId,
        [property: JsonPropertyName("message_id")] string MessageId,
        [property: JsonPropertyName("from")] string? From,
        [property: JsonPropertyName("to")] IReadOnlyList<string> To,
        [property: JsonPropertyName("cc")] IReadOnlyList<string> Cc,
        [property: JsonPropertyName("bcc")] IReadOnlyList<string> Bcc,
        [property: JsonPropertyName("send_mode")] SendMode SendMode,
        [property: JsonPropertyName("subject")] string? Subject,
        [property: JsonPropertyName("text_preview")] string? TextPreview,
        [property: JsonPropertyName("attachment_count")] int AttachmentCount,
        [property: JsonPropertyName("attachment_names")] IReadOnlyList<string> AttachmentNames,
        [property: JsonPropertyName("prepared_at")] DateTimeOffset PreparedAt,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,
        [property: JsonPropertyName("content_hash")] string ContentHash,
        [property: JsonPropertyName("idempotency_key_hash")] string IdempotencyKeyHash)
    {
        public static StoredPreview Of(SendPreview preview) => new(
            preview.PreparationId,
            preview.AccountId,
            preview.MessageId,
            preview.From,
            preview.To,
            preview.Cc,
            preview.Bcc,
            preview.SendMode,
            preview.Subject,
            preview.TextPreview,
            preview.AttachmentCount,
            preview.AttachmentNames,
            preview.PreparedAt,
            preview.ExpiresAt,
            preview.ContentHash,
            preview.IdempotencyKeyHash);

        public SendPreview ToSendPreview() => new(
            PreparationId,
            AccountId,
            MessageId,
            From,
            To,
            Cc,
            Bcc,
            SendMode,
            Subject,
            TextPreview,
            AttachmentCount,
            AttachmentNames,
            PreparedAt,
            ExpiresAt,
            ContentHash,
            IdempotencyKeyHash,
            ConfirmationToken: string.Empty);
    }
}
