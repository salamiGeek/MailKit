using MailKit;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Connections;
using MailKit.Net.Smtp;
using MimeKit;

namespace MailKit.Agent.Mail.Smtp;

/// <summary>
/// Delivers exactly one prepared message over SMTP and reports a conservative terminal
/// outcome. The MIME bytes are the DATA payload (they never carry Bcc headers) while
/// blind-copy recipients travel only in the SMTP envelope. Before the single
/// <see cref="SmtpClient.SendAsync(FormatOptions, MimeMessage, MailboxAddress,
/// IEnumerable{MailboxAddress}, CancellationToken)"/> call the gateway checks the
/// server's SIZE limit and SMTPUTF8 capability so unsatisfiable sends fail before any
/// DATA traffic. Once the send has started, ambiguous transport failures (I/O errors,
/// timeouts, protocol disconnects) are reported as
/// <see cref="SendState.Indeterminate"/> and the gateway never reconnects or resends;
/// a non-throwing disconnect always runs in the cleanup path.
/// </summary>
public sealed class SmtpGateway : ISmtpGateway
{
    private readonly ISmtpClientFactory clientFactory;
    private readonly TimeSpan commandTimeout;

    public SmtpGateway()
        : this(new SmtpClientFactory())
    {
    }

    public SmtpGateway(ISmtpClientFactory clientFactory, TimeSpan? commandTimeout = null)
    {
        this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        this.commandTimeout = commandTimeout ?? ConnectionLimits.Default.CommandTimeout;
        if (this.commandTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(commandTimeout));
    }

    public async Task<SendTransportOutcome> SendAsync(
        AccountProfile profile,
        PasswordCredentialLease credential,
        PreparedOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(message);

        SmtpClient? client = null;
        try
        {
            try
            {
                client = await clientFactory.CreateAsync(profile, credential, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (MailOperationException exception)
            {
                // Connection or authentication failure before any send activity.
                return SendTransportOutcome.Failed(exception.Error);
            }
            catch (Exception exception)
            {
                return SendTransportOutcome.Failed(
                    ProtocolExceptionMapper.Map(exception, "smtp", "authenticate", cancellationToken).Error);
            }

            return await DeliverAsync(client, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (client is not null)
                await DisconnectAndDisposeAsync(client).ConfigureAwait(false);
        }
    }

    private async Task<SendTransportOutcome> DeliverAsync(
        SmtpClient client,
        PreparedOutgoingMessage message,
        CancellationToken cancellationToken)
    {
        MimeMessage mime;
        try
        {
            using var buffer = new MemoryStream(message.MimeMessage, writable: false);
            mime = await MimeMessage.LoadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SendTransportOutcome.Failed(MessageInvalid());
        }

        // Defense-in-depth: the prepared MIME must never carry Bcc headers.
        mime.Bcc.Clear();

        MailboxAddress sender;
        List<MailboxAddress> recipients;
        try
        {
            sender = ResolveSender(message, mime);
            recipients = message.EnvelopeRecipients
                .Select(recipient => ParseEnvelopeMailbox(recipient, "recipient"))
                .ToList();
        }
        catch (MailOperationException exception)
        {
            return SendTransportOutcome.Failed(exception.Error);
        }

        if (recipients.Count == 0)
            return SendTransportOutcome.Failed(ValidationError(
                "smtp.missing_recipients", "The prepared message has no envelope recipients."));

        if (client.MaxSize > 0 && message.MimeMessage.LongLength > client.MaxSize)
            return SendTransportOutcome.Failed(ValidationError(
                "smtp.size_exceeded",
                $"The message exceeds the server's advertised size limit of {client.MaxSize} bytes."));

        if (RequiresSmtpUtf8(mime, sender, recipients) &&
            (client.Capabilities & SmtpCapabilities.UTF8) == 0)
        {
            return SendTransportOutcome.Failed(new ToolError(
                "smtp.smtputf8_required",
                ErrorCategory.Capability,
                "The message needs SMTPUTF8, which the server does not advertise.",
                false,
                null,
                Details()));
        }

        bool sendStarted = false;
        try
        {
            using var scope = CommandTimeoutScope.Create(commandTimeout, cancellationToken);
            sendStarted = true;
            await client.SendAsync(
                FormatOptions.Default, mime, sender, recipients, scope.Token).ConfigureAwait(false);
            return SendTransportOutcome.Succeeded();
        }
        catch (SmtpCommandException exception)
        {
            // The server definitively rejected the send; nothing was delivered.
            return SendTransportOutcome.Failed(MapRejection(exception));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!sendStarted)
        {
            return SendTransportOutcome.Failed(
                ProtocolExceptionMapper.Map(exception, "smtp", "send_commit", cancellationToken).Error);
        }
        catch (OperationCanceledException)
        {
            // Command timeout after the send began: the outcome is unknown.
            return SendTransportOutcome.Indeterminate(TransportUnknown());
        }
        catch (Exception)
        {
            // Any other transport failure after the send began (I/O error, protocol
            // disconnect, and so on) is ambiguous for the server side; the gateway
            // never reconnects and resends.
            return SendTransportOutcome.Indeterminate(TransportUnknown());
        }
    }

    private static MailboxAddress ResolveSender(
        PreparedOutgoingMessage message, MimeMessage mime)
    {
        if (!string.IsNullOrEmpty(message.EnvelopeSender))
            return ParseEnvelopeMailbox(message.EnvelopeSender, "sender");

        MailboxAddress? from = mime.From.Mailboxes.FirstOrDefault();
        if (from is not null)
            return from;

        throw MailOperationExceptionFromError(ValidationError(
            "smtp.sender_missing", "The prepared message has no envelope sender."));
    }

    private static MailboxAddress ParseEnvelopeMailbox(string address, string role)
    {
        if (!MailboxAddress.TryParse(address, out MailboxAddress? mailbox))
        {
            throw MailOperationExceptionFromError(ValidationError(
                "smtp.invalid_recipient", $"The envelope {role} address has an invalid format."));
        }

        return mailbox!;
    }

    private static bool RequiresSmtpUtf8(
        MimeMessage mime, MailboxAddress sender, IReadOnlyList<MailboxAddress> recipients) =>
        sender.IsInternational ||
        recipients.Any(recipient => recipient.IsInternational) ||
        mime.From.Mailboxes.Any(mailbox => mailbox.IsInternational) ||
        mime.To.Mailboxes.Any(mailbox => mailbox.IsInternational) ||
        mime.Cc.Mailboxes.Any(mailbox => mailbox.IsInternational);

    private static ToolError MapRejection(SmtpCommandException exception) => exception.ErrorCode switch
    {
        SmtpErrorCode.RecipientNotAccepted => new ToolError(
            "smtp.recipient_rejected", ErrorCategory.Validation,
            "The SMTP server rejected a recipient.", false, null, Details()),
        SmtpErrorCode.SenderNotAccepted => new ToolError(
            "smtp.sender_rejected", ErrorCategory.Validation,
            "The SMTP server rejected the sender.", false, null, Details()),
        SmtpErrorCode.MessageNotAccepted => new ToolError(
            "smtp.message_rejected", ErrorCategory.Transient,
            "The SMTP server rejected the message.", false, null, Details()),
        _ => new ToolError(
            "smtp.rejected", ErrorCategory.Transient,
            "The SMTP server rejected the send operation.", false, null, Details())
    };

    private async Task DisconnectAndDisposeAsync(SmtpClient client)
    {
        try
        {
            if (client.IsConnected)
            {
                using var scope = CommandTimeoutScope.Create(commandTimeout, CancellationToken.None);
                await client.DisconnectAsync(true, scope.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Cleanup cannot replace the stable result of the requested operation.
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // Cleanup cannot replace the stable result of the requested operation.
            }
        }
    }

    private static ToolError MessageInvalid() => ValidationError(
        "smtp.message_invalid", "The prepared message could not be parsed for delivery.");

    private static ToolError TransportUnknown() => new(
        "smtp.transport_unknown",
        ErrorCategory.Transient,
        "The SMTP transport failed after the send began; the server may still accept the message.",
        false,
        null,
        Details());

    private static ToolError ValidationError(string code, string message) =>
        new(code, ErrorCategory.Validation, message, false, null, Details());

    private static MailOperationException MailOperationExceptionFromError(ToolError error) =>
        new(error);

    private static Dictionary<string, string> Details() => new() { ["protocol"] = "smtp" };
}
