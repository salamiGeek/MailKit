using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Crash-safe send idempotency ledger. One JSON file per account and idempotency-key
/// hash under <c>&lt;data&gt;/send-ledger/&lt;account_id&gt;/&lt;idempotency_hash&gt;.json</c>,
/// written through a create-new temporary file, flushed, and atomically moved over the
/// destination. All operations on one key — load, create, and transition — are
/// serialized by a per-key asynchronous lock held across both the state validation and
/// the durable write, so concurrent callers can never both observe the previous state
/// and both proceed. An <see cref="SendState.Attempting"/> record left behind by an
/// earlier process is treated as terminal <see cref="SendState.Indeterminate"/> on load
/// and can never trigger another SMTP invocation.
/// </summary>
public sealed class JsonSendLedger : ISendLedger
{
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    // Keyed by the full entry path so every ledger instance in this process that
    // targets the same record is serialized, not just callers of one instance.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> KeyLocks = new();

    private readonly string ledgerDirectory;
    private readonly object gate = new();
    private readonly HashSet<string> liveAttemptingKeys = new(StringComparer.Ordinal);

    public JsonSendLedger(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ledgerDirectory = Path.Combine(dataDirectory, "send-ledger");
    }

    public async Task<SendLedgerEntry?> FindAsync(
        string accountId, string idempotencyKeyHash, CancellationToken cancellationToken)
    {
        string path = GetEntryPath(accountId, idempotencyKeyHash);
        SemaphoreSlim keyLock = KeyLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Load(path, accountId, idempotencyKeyHash);
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<SendLedgerEntry> CreateAsync(
        SendLedgerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.State != SendState.Prepared)
        {
            throw new ArgumentException(
                "Only Prepared entries can be created.", nameof(entry));
        }

        ValidateKeyArguments(entry.AccountId, entry.IdempotencyKeyHash);
        string path = GetEntryPath(entry.AccountId, entry.IdempotencyKeyHash);
        SemaphoreSlim keyLock = KeyLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SendLedgerEntry? existing = Load(path, entry.AccountId, entry.IdempotencyKeyHash);
            if (existing is not null && existing.State != SendState.Prepared)
            {
                throw new InvalidOperationException(
                    "A terminal send ledger record already exists for this idempotency key.");
            }

            await WriteAsync(entry, path, cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            keyLock.Release();
        }
    }

    public async Task<SendLedgerEntry> TransitionAsync(
        string accountId,
        string idempotencyKeyHash,
        SendState targetState,
        DateTimeOffset timestamp,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        ValidateKeyArguments(accountId, idempotencyKeyHash);
        if (!Enum.IsDefined(targetState))
            throw new ArgumentOutOfRangeException(nameof(targetState));

        string path = GetEntryPath(accountId, idempotencyKeyHash);
        string key = Key(accountId, idempotencyKeyHash);
        SemaphoreSlim keyLock = KeyLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await keyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SendLedgerEntry current = Load(path, accountId, idempotencyKeyHash)
                ?? throw new InvalidOperationException(
                    "No send ledger record exists for this idempotency key.");
            SendState currentState = current.State;
            if (!IsAllowedTransition(currentState, targetState))
            {
                throw new InvalidOperationException(
                    $"The send state transition {currentState} -> {targetState} is not allowed.");
            }

            SendLedgerEntry updated = targetState switch
            {
                SendState.Attempting => current with
                {
                    State = SendState.Attempting,
                    AttemptedAt = timestamp,
                    CorrelationId = correlationId
                },
                _ => current with
                {
                    State = targetState,
                    CompletedAt = timestamp,
                    CorrelationId = correlationId ?? current.CorrelationId
                }
            };

            if (targetState == SendState.Attempting)
            {
                lock (gate)
                {
                    liveAttemptingKeys.Add(key);
                }
            }

            try
            {
                await WriteAsync(updated, path, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (targetState == SendState.Attempting)
                {
                    lock (gate)
                    {
                        liveAttemptingKeys.Remove(key);
                    }
                }

                throw;
            }

            if (targetState != SendState.Attempting)
            {
                lock (gate)
                {
                    liveAttemptingKeys.Remove(key);
                }
            }

            return updated;
        }
        finally
        {
            keyLock.Release();
        }
    }

    private static bool IsAllowedTransition(SendState current, SendState target) =>
        (current, target) switch
        {
            (SendState.Prepared, SendState.Attempting) => true,
            (SendState.Attempting, SendState.Succeeded) => true,
            (SendState.Attempting, SendState.Failed) => true,
            (SendState.Attempting, SendState.Indeterminate) => true,
            // Terminal outcome of a drafts-mode commit: the message was appended to
            // the Drafts folder and nothing was delivered; the record is final.
            (SendState.Attempting, SendState.Drafted) => true,
            _ => false
        };

    private SendLedgerEntry? Load(string path, string accountId, string idempotencyKeyHash)
    {
        if (!File.Exists(path))
            return null;

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        var entry = JsonSerializer.Deserialize<SendLedgerEntry>(stream);
        if (entry is null)
            return null;

        bool live;
        lock (gate)
        {
            live = liveAttemptingKeys.Contains(Key(accountId, idempotencyKeyHash));
        }

        return entry.State == SendState.Attempting && !live
            ? entry with { State = SendState.Indeterminate }
            : entry;
    }

    private async Task WriteAsync(
        SendLedgerEntry entry, string path, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, entry, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await MoveOverDestinationAsync(temporary, path, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task MoveOverDestinationAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private string GetEntryPath(string accountId, string idempotencyKeyHash)
    {
        ValidateKeyArguments(accountId, idempotencyKeyHash);
        return Path.Combine(ledgerDirectory, accountId, idempotencyKeyHash + ".json");
    }

    private static void ValidateKeyArguments(string accountId, string idempotencyKeyHash)
    {
        if (!AccountProfileValidator.ValidateId(accountId))
            throw new ArgumentException("Account ID has an invalid format.", nameof(accountId));
        if (!HashPattern.IsMatch(idempotencyKeyHash))
            throw new ArgumentException(
                "Idempotency key hash must be 64 lowercase hexadecimal characters.",
                nameof(idempotencyKeyHash));
    }

    private static string Key(string accountId, string idempotencyKeyHash) =>
        accountId + "/" + idempotencyKeyHash;
}
