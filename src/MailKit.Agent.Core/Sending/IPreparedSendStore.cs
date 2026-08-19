namespace MailKit.Agent.Core.Sending;

public interface IPreparedSendStore
{
    Task AddAsync(PreparedOutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Removes and returns the preparation with the given ID in a single atomic step.
    /// Expired preparations are cleared (their MIME byte arrays are zeroed) and
    /// reported as missing.
    /// </summary>
    Task<PreparedOutgoingMessage?> TakeAsync(string preparationId, CancellationToken cancellationToken);
}
