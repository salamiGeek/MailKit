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
/// The serialized MIME must exclude Bcc recipients; Bcc delivery is handled purely
/// through the SMTP envelope built from the prepared message metadata.
/// </summary>
public interface IOutgoingMessageComposer
{
    Task<ComposedOutgoingMessage> ComposeAsync(
        AccountProfile profile,
        OutgoingMessageDraft draft,
        CancellationToken cancellationToken);
}
