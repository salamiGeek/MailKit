using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// The output of composing a draft: the serialized MIME bytes (which must never
/// contain Bcc headers) and the deterministic Message-Id chosen for the message.
/// </summary>
public sealed record ComposedOutgoingMessage(
    byte[] MimeMessage,
    string MessageId);

/// <summary>
/// Composes a validated draft into MIME bytes plus a deterministic Message-Id.
/// The Message-Id is derived from <paramref name="idempotencyKey"/> (and the account),
/// and the content hash the caller computes from the draft are both date-independent,
/// so a re-prepare of the same key yields the same identity. The serialized MIME
/// includes a <c>Date</c> header taken from the implementation's clock, so byte
/// identity holds only for identical clock readings; protection against redelivery
/// comes from the send ledger keyed by the idempotency key, not from byte identity.
/// The serialized MIME must exclude Bcc recipients; Bcc delivery is handled purely
/// through the SMTP envelope built from the prepared message metadata.
/// </summary>
public interface IOutgoingMessageComposer
{
    /// <summary>
    /// Composes a draft into MIME bytes plus the Message-Id derived
    /// from <paramref name="idempotencyKey"/> so the same key always maps to the
    /// same message identity regardless of when composition runs.
    /// </summary>
    Task<ComposedOutgoingMessage> ComposeAsync(
        AccountProfile profile,
        OutgoingMessageDraft draft,
        string idempotencyKey,
        CancellationToken cancellationToken);
}
