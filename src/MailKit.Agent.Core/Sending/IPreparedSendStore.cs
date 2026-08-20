namespace MailKit.Agent.Core.Sending;

public interface IPreparedSendStore
{
    Task AddAsync(PreparedOutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the preparation with the given ID WITHOUT removing it, so callers
    /// can inspect (peek) a preparation — for example to render its preview for
    /// local approval — before deciding to consume it with <see cref="TakeAsync"/>.
    /// Expired preparations are swept exactly like <see cref="TakeAsync"/> and
    /// reported as missing.
    /// </summary>
    Task<PreparedOutgoingMessage?> TryGetAsync(string preparationId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes and returns the preparation with the given ID in a single atomic step.
    /// Expired preparations are cleared (their MIME byte arrays are zeroed) and
    /// reported as missing.
    /// </summary>
    Task<PreparedOutgoingMessage?> TakeAsync(string preparationId, CancellationToken cancellationToken);
}
