namespace MailKit.Agent.Core.Sending;

public interface ISendLedger
{
    Task<SendLedgerEntry?> FindAsync(
        string accountId, string idempotencyKeyHash, CancellationToken cancellationToken);

    /// <summary>
    /// Persists a new <see cref="SendState.Prepared"/> record. A stale
    /// <see cref="SendState.Prepared"/> record (an earlier prepare that never
    /// committed) is replaced; any other existing record is rejected.
    /// </summary>
    Task<SendLedgerEntry> CreateAsync(
        SendLedgerEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Applies one allowed state transition
    /// (<c>Prepared -> Attempting</c> or <c>Attempting -> Succeeded | Failed | Indeterminate | Drafted</c>)
    /// and returns the updated entry. Disallowed transitions, terminal records, and
    /// unknown records are rejected. An <c>Attempting</c> record written by an earlier
    /// process loads as terminal <c>Indeterminate</c>.
    /// </summary>
    Task<SendLedgerEntry> TransitionAsync(
        string accountId,
        string idempotencyKeyHash,
        SendState targetState,
        DateTimeOffset timestamp,
        string? correlationId,
        CancellationToken cancellationToken);
}
