using System.Security.Cryptography;
using System.Text;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mail.Attachments;
using MimeKit;

namespace MailKit.Agent.Mail.Smtp;

/// <summary>
/// Composes an <see cref="OutgoingMessageDraft"/> into MIME bytes plus a deterministic
/// Message-Id using MimeKit. The Message-Id is the lowercase base64url SHA-256 of
/// <c>account_id + NUL + idempotency_key</c> followed by <c>@mailkit-agent.local</c>,
/// so it is stable for a given idempotency key regardless of when composition runs.
/// The serialized MIME carries a <c>Date</c> header taken from the injected
/// <see cref="TimeProvider"/> (MimeKit's default constructor would stamp the wall
/// clock), so identical bytes are produced only for identical clock readings;
/// redelivery protection comes from the send ledger keyed by the idempotency key,
/// not from byte identity. The serialized MIME never contains Bcc headers; blind-copy
/// delivery happens exclusively through the SMTP envelope built by
/// <see cref="SendApplication"/>. Attachment files are opened through
/// <see cref="UploadAttachmentPathPolicy"/> so traversal, reparse-point, and identity
/// attacks cannot reach arbitrary files.
/// </summary>
public sealed class OutgoingMessageComposer : IOutgoingMessageComposer
{
    /// <summary>The right-hand side of every generated Message-Id.</summary>
    public const string MessageIdDomain = "mailkit-agent.local";

    /// <summary>The default serialized message ceiling (25 MiB, matching attachments).</summary>
    public const long DefaultMaxMessageBytes = 25 * 1024 * 1024;

    private readonly MailFileOptions mailFileOptions;
    private readonly long maxMessageBytes;
    private readonly TimeProvider timeProvider;

    public OutgoingMessageComposer(
        MailFileOptions? mailFileOptions = null,
        long? maxMessageBytes = null,
        TimeProvider? timeProvider = null)
    {
        this.mailFileOptions = mailFileOptions ?? MailFileOptionsResolver.Resolve(AppDataPaths.Resolve());
        this.maxMessageBytes = maxMessageBytes ?? DefaultMaxMessageBytes;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.maxMessageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
    }

    public async Task<ComposedOutgoingMessage> ComposeAsync(
        AccountProfile profile,
        OutgoingMessageDraft draft,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        List<MailboxAddress> to = ParseMailboxes(draft.To);
        List<MailboxAddress> cc = ParseMailboxes(draft.Cc);
        // Bcc is validated for format only; it is never attached to the message
        // headers because the serialized MIME becomes the DATA payload.
        ParseMailboxes(draft.Bcc);
        if (to.Count + cc.Count == 0)
            throw ValidationError(
                "validation.missing_recipients", "At least one recipient is required.");

        MailboxAddress sender = draft.From is null
            ? RequireMailbox(profile.Username, "sender")
            : new MailboxAddress(draft.From.DisplayName, RequireMailbox(draft.From.Address, "sender").Address);

        if (string.IsNullOrEmpty(draft.TextBody) && string.IsNullOrEmpty(draft.HtmlBody))
            throw ValidationError("validation.missing_body", "A text or HTML body is required.");

        byte[] seed = SHA256.HashData(Encoding.UTF8.GetBytes(profile.Id + "\0" + idempotencyKey));
        string seedHex = Convert.ToHexString(seed).ToLowerInvariant();

        var message = new MimeMessage
        {
            MessageId = $"{ToBase64Url(seed)}@{MessageIdDomain}"
        };
        // Pin Date to the injected clock; MimeKit's constructor stamps DateTimeOffset.Now.
        message.Date = timeProvider.GetUtcNow();
        if (draft.Subject is not null)
            message.Subject = draft.Subject;
        message.From.Add(sender);
        foreach (MailboxAddress recipient in to)
            message.To.Add(recipient);
        foreach (MailboxAddress recipient in cc)
            message.Cc.Add(recipient);

        var builder = new BodyBuilder();
        string? normalizedText = NormalizeCrlf(draft.TextBody);
        string? normalizedHtml = NormalizeCrlf(draft.HtmlBody);
        if (normalizedText is not null)
            builder.TextBody = normalizedText;
        if (normalizedHtml is not null)
            builder.HtmlBody = normalizedHtml;
        if (draft.AttachmentPaths is { Count: > 0 } attachmentPaths)
            AddAttachments(builder, attachmentPaths);

        message.Body = builder.ToMessageBody();
        MakeBoundariesDeterministic(message.Body, seedHex);

        byte[] mimeMessage = await SerializeAsync(message, cancellationToken).ConfigureAwait(false);
        if (mimeMessage.LongLength > maxMessageBytes)
        {
            CryptographicOperations.ZeroMemory(mimeMessage);
            throw ValidationError(
                "validation.message_too_large",
                $"The composed message must be {maxMessageBytes} bytes or fewer.");
        }

        return new ComposedOutgoingMessage(mimeMessage, message.MessageId);
    }

    private void AddAttachments(
        BodyBuilder builder, IReadOnlyList<string> attachmentPaths)
    {
        if (mailFileOptions.UploadRoots.Count == 0)
        {
            throw ValidationError(
                "attachment.upload_roots_required",
                "SMTP attachments require an explicitly configured upload root.");
        }

        using var policy = new UploadAttachmentPathPolicy(mailFileOptions.UploadRoots);
        foreach (string path in attachmentPaths)
        {
            Stream content = policy.OpenRead(path);
            using (content)
            {
                if (content.Length > maxMessageBytes)
                    throw ValidationError(
                        "validation.message_too_large",
                        $"An attachment must be {maxMessageBytes} bytes or fewer.");

                builder.Attachments.Add(Path.GetFileName(path), content, CancellationToken.None);
            }
        }
    }

    private static string ToBase64Url(byte[] hash) =>
        Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_')
            .ToLowerInvariant();

    private static string? NormalizeCrlf(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    private static void MakeBoundariesDeterministic(MimeEntity? entity, string seedHex)
    {
        if (entity is not Multipart multipart)
            return;

        int counter = 0;
        AssignBoundary(multipart, seedHex, ref counter);
    }

    private static void AssignBoundary(Multipart multipart, string seedHex, ref int counter)
    {
        multipart.Boundary = $"mailkit-agent-{seedHex}-{counter++}";
        foreach (MimeEntity child in multipart)
        {
            if (child is Multipart nested)
                AssignBoundary(nested, seedHex, ref counter);
        }
    }

    private static async Task<byte[]> SerializeAsync(
        MimeMessage message, CancellationToken cancellationToken)
    {
        FormatOptions options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;

        using var buffer = new MemoryStream();
        await message.WriteToAsync(options, buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static List<MailboxAddress> ParseMailboxes(IReadOnlyList<OutgoingMailbox>? mailboxes)
    {
        if (mailboxes is null || mailboxes.Count == 0)
            return [];

        var parsed = new List<MailboxAddress>(mailboxes.Count);
        foreach (OutgoingMailbox mailbox in mailboxes)
            parsed.Add(new MailboxAddress(mailbox.DisplayName, RequireMailbox(mailbox.Address, "recipient").Address));
        return parsed;
    }

    private static MailboxAddress RequireMailbox(string? address, string role)
    {
        // MimeKit's TryParse accepts domain-less addr-specs, so a structural check
        // keeps this defense-in-depth aligned with the Core-side validation.
        if (address is null ||
            !MailboxAddress.TryParse(address, out MailboxAddress? mailbox) ||
            !HasRoutedDomain(address))
        {
            throw ValidationError(
                "validation.invalid_recipient",
                $"The {role} address has an invalid format.");
        }

        return mailbox!;
    }

    private static bool HasRoutedDomain(string address)
    {
        int at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1;
    }

    private static MailOperationException ValidationError(string code, string message) =>
        new(new ToolError(code, ErrorCategory.Validation, message, false, null, null));
}
