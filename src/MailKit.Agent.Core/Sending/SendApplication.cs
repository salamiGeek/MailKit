using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Storage;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Protocol-agnostic two-phase send workflow. <see cref="PrepareAsync"/> validates a
/// draft, composes MIME once, and stores an in-memory preparation with an HMAC
/// confirmation token without acquiring credentials or an SMTP connection.
/// <see cref="CommitAsync"/> consumes the one-time token, writes the ledger
/// <see cref="SendState.Attempting"/> record before any network I/O, acquires a fresh
/// password lease, invokes SMTP exactly once, persists the returned terminal state,
/// and disposes secrets and MIME bytes.
/// </summary>
public sealed class SendApplication
{
    public const int MaxSubjectLength = 998;
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxSessionIdLength = 128;
    public const int TextPreviewLength = 200;

    public static readonly TimeSpan DefaultConfirmationTtl = TimeSpan.FromMinutes(10);

    private static readonly Regex IdempotencyKeyPattern =
        new("^[A-Za-z0-9._-]{1,128}$", RegexOptions.CultureInvariant);
    private static readonly Regex MailboxAddressPattern =
        new("^[A-Za-z0-9._%+'-]{1,64}@[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$",
            RegexOptions.CultureInvariant);

    private readonly IAccountProfileStore accountStore;
    private readonly IAccountCredentialVault credentialVault;
    private readonly IOutgoingMessageComposer composer;
    private readonly ISmtpGateway smtpGateway;
    private readonly ISendConfirmationCodec confirmationCodec;
    private readonly IPreparedSendStore preparedStore;
    private readonly ISendLedger sendLedger;
    private readonly OperationPolicy policy;
    private readonly MailSafetyLimits safetyLimits;
    private readonly MailFileOptions mailFileOptions;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan confirmationTtl;

    public SendApplication(
        IAccountProfileStore accountStore,
        IAccountCredentialVault credentialVault,
        IOutgoingMessageComposer composer,
        ISmtpGateway smtpGateway,
        ISendConfirmationCodec confirmationCodec,
        IPreparedSendStore preparedStore,
        ISendLedger sendLedger,
        OperationPolicy policy,
        MailSafetyLimits safetyLimits,
        TimeProvider? timeProvider = null,
        TimeSpan? confirmationTtl = null,
        MailFileOptions? mailFileOptions = null)
    {
        this.accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        this.credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        this.composer = composer ?? throw new ArgumentNullException(nameof(composer));
        this.smtpGateway = smtpGateway ?? throw new ArgumentNullException(nameof(smtpGateway));
        this.confirmationCodec = confirmationCodec ?? throw new ArgumentNullException(nameof(confirmationCodec));
        this.preparedStore = preparedStore ?? throw new ArgumentNullException(nameof(preparedStore));
        this.sendLedger = sendLedger ?? throw new ArgumentNullException(nameof(sendLedger));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.safetyLimits = safetyLimits ?? throw new ArgumentNullException(nameof(safetyLimits));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.confirmationTtl = confirmationTtl ?? DefaultConfirmationTtl;
        if (this.confirmationTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(confirmationTtl));
        this.mailFileOptions = mailFileOptions ??
            MailFileOptionsResolver.Resolve(AppDataPaths.Resolve());
    }

    public async Task<ToolResult<SendPreview>> PrepareAsync(
        string accountId,
        OutgoingMessageDraft? draft,
        string idempotencyKey,
        string sessionId,
        CancellationToken cancellationToken)
    {
        string correlationId = CorrelationId();
        if (!AccountProfileValidator.ValidateId(accountId))
        {
            return AccountOperationBoundary.Failure<SendPreview>(
                "account.invalid_id", ErrorCategory.Validation,
                "The account ID is invalid.", correlationId);
        }

        if (!TryValidateDraft(draft, safetyLimits, out ToolError? draftError))
            return ToolResult<SendPreview>.Failure(draftError!, correlationId);

        if (!TryValidateAttachmentPaths(draft!, out ToolError? attachmentError))
            return ToolResult<SendPreview>.Failure(attachmentError!, correlationId);

        if (!IsWellFormedIdempotencyKey(idempotencyKey))
        {
            return ValidationFailure<SendPreview>(
                "validation.invalid_idempotency_key",
                "The idempotency key format is invalid.", correlationId);
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > MaxSessionIdLength)
        {
            return ValidationFailure<SendPreview>(
                "validation.invalid_session",
                "The session ID is required.", correlationId);
        }

        int recipientCount = Recipients(draft!).Count;
        PolicyDecision initialDecision = policy.Evaluate(new OperationDescriptor(
            "send_prepare", RiskLevel.RecoverableWrite, recipientCount, 0));
        if (!initialDecision.Allowed)
            return ToolResult<SendPreview>.Failure(initialDecision.Error!, correlationId);

        try
        {
            AccountProfile? profile = await accountStore.GetAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
            {
                return AccountOperationBoundary.Failure<SendPreview>(
                    "account.not_found", ErrorCategory.Validation,
                    "The account was not found.", correlationId);
            }

            if (!string.Equals(profile.Id, accountId, StringComparison.Ordinal) ||
                AccountProfileValidator.Validate(profile).Count != 0)
            {
                return AccountOperationBoundary.Failure<SendPreview>(
                    "account.invalid_profile", ErrorCategory.Validation,
                    "The stored account profile is invalid.", correlationId);
            }

            if (profile.Smtp is null)
            {
                return AccountOperationBoundary.Failure<SendPreview>(
                    "smtp.not_configured", ErrorCategory.Capability,
                    "SMTP is not configured for this account.", correlationId);
            }

            string idempotencyKeyHash = Hash(idempotencyKey);
            SendLedgerEntry? existing = await sendLedger.FindAsync(
                accountId, idempotencyKeyHash, cancellationToken).ConfigureAwait(false);
            if (existing is { State: not SendState.Prepared })
            {
                return ToolResult<SendPreview>.Failure(IdempotencyConflict(correlationId), correlationId);
            }

            ComposedOutgoingMessage composed = await composer.ComposeAsync(
                profile, draft!, cancellationToken).ConfigureAwait(false);
            string contentHash = ComputeContentHash(composed.MessageId, draft!);

            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset expiresAt = now.Add(confirmationTtl);
            string preparationId = Guid.NewGuid().ToString("N");

            var previewWithoutToken = new SendPreview(
                preparationId,
                accountId,
                composed.MessageId,
                draft!.From?.DisplayText,
                Display(draft.To),
                Display(draft.Cc),
                Display(draft.Bcc),
                draft.Subject,
                PreviewText(draft.TextBody),
                draft.AttachmentPaths?.Count ?? 0,
                (draft.AttachmentPaths ?? [])
                    .Select(path => Path.GetFileName(path) ?? string.Empty)
                    .ToArray(),
                now,
                expiresAt,
                contentHash,
                idempotencyKeyHash,
                string.Empty);
            string confirmationToken = confirmationCodec.Encode(new SendConfirmationPayload(
                preparationId, accountId, contentHash, idempotencyKeyHash, sessionId, expiresAt));
            SendPreview preview = previewWithoutToken with { ConfirmationToken = confirmationToken };

            var prepared = new PreparedOutgoingMessage(
                preparationId,
                accountId,
                composed.MessageId,
                contentHash,
                composed.MimeMessage,
                draft.From?.Address,
                Recipients(draft).Select(mailbox => mailbox.Address).ToArray(),
                preview,
                idempotencyKeyHash,
                expiresAt);

            await sendLedger.CreateAsync(
                new SendLedgerEntry(
                    accountId, idempotencyKeyHash, composed.MessageId,
                    SendState.Prepared, now, null, null, correlationId),
                cancellationToken).ConfigureAwait(false);
            await preparedStore.AddAsync(prepared, cancellationToken).ConfigureAwait(false);

            ToolResult<SendPreview> success = ToolResult<SendPreview>.Success(preview, correlationId);
            int bytes = JsonSerializer.SerializeToUtf8Bytes(success).Length;
            PolicyDecision resultDecision = policy.Evaluate(new OperationDescriptor(
                "send_prepare", RiskLevel.RecoverableWrite, recipientCount, bytes));
            return resultDecision.Allowed
                ? success
                : ToolResult<SendPreview>.Failure(resultDecision.Error!, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailOperationException exception)
        {
            return ToolResult<SendPreview>.Failure(exception.Error, correlationId);
        }
        catch (InvalidOperationException)
        {
            return ToolResult<SendPreview>.Failure(IdempotencyConflict(correlationId), correlationId);
        }
        catch (Exception)
        {
            return AccountOperationBoundary.Failure<SendPreview>(
                "mail.operation_failed", ErrorCategory.Internal,
                "The send preparation failed.", correlationId);
        }
    }

    public async Task<ToolResult<SendStatus>> CommitAsync(
        string confirmationToken,
        string sessionId,
        CancellationToken cancellationToken)
    {
        string correlationId = CorrelationId();

        SendConfirmationPayload payload;
        try
        {
            payload = confirmationCodec.Decode(confirmationToken ?? string.Empty);
        }
        catch (InvalidSendConfirmationException)
        {
            return ValidationFailure<SendStatus>(
                "send.invalid_confirmation",
                "The send confirmation is invalid or expired.", correlationId);
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > MaxSessionIdLength ||
            !string.Equals(payload.SessionId, sessionId, StringComparison.Ordinal))
        {
            return ValidationFailure<SendStatus>(
                "send.session_mismatch",
                "The send confirmation does not belong to this session.", correlationId);
        }

        PreparedOutgoingMessage? message = await preparedStore.TakeAsync(
            payload.PreparationId, cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return ValidationFailure<SendStatus>(
                "send.preparation_not_found",
                "The prepared message is unknown, expired, or already consumed.", correlationId);
        }

        try
        {
            if (!string.Equals(message.AccountId, payload.AccountId, StringComparison.Ordinal) ||
                !string.Equals(message.ContentHash, payload.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(message.IdempotencyKeyHash, payload.IdempotencyKeyHash, StringComparison.Ordinal))
            {
                return ValidationFailure<SendStatus>(
                    "send.confirmation_mismatch",
                    "The send confirmation does not match the prepared message.", correlationId);
            }

            PolicyDecision initialDecision = policy.Evaluate(new OperationDescriptor(
                "send_commit",
                RiskLevel.ExternalOrIrreversible,
                Math.Max(1, message.EnvelopeRecipients.Count),
                0));
            if (!initialDecision.Allowed)
                return ToolResult<SendStatus>.Failure(initialDecision.Error!, correlationId);

            AccountProfile? profile = await accountStore.GetAsync(
                payload.AccountId, cancellationToken).ConfigureAwait(false);
            if (profile is null)
            {
                return AccountOperationBoundary.Failure<SendStatus>(
                    "account.not_found", ErrorCategory.Validation,
                    "The account was not found.", correlationId);
            }

            if (!string.Equals(profile.Id, payload.AccountId, StringComparison.Ordinal) ||
                AccountProfileValidator.Validate(profile).Count != 0)
            {
                return AccountOperationBoundary.Failure<SendStatus>(
                    "account.invalid_profile", ErrorCategory.Validation,
                    "The stored account profile is invalid.", correlationId);
            }

            if (profile.Smtp is null)
            {
                return AccountOperationBoundary.Failure<SendStatus>(
                    "smtp.not_configured", ErrorCategory.Capability,
                    "SMTP is not configured for this account.", correlationId);
            }

            SendLedgerEntry? existing = await sendLedger.FindAsync(
                payload.AccountId, payload.IdempotencyKeyHash, cancellationToken).ConfigureAwait(false);
            if (existing is { State: not SendState.Prepared })
                return ToolResult<SendStatus>.Failure(
                    IdempotencyConflict(correlationId), correlationId);

            await sendLedger.TransitionAsync(
                payload.AccountId,
                payload.IdempotencyKeyHash,
                SendState.Attempting,
                timeProvider.GetUtcNow(),
                correlationId,
                cancellationToken).ConfigureAwait(false);

            ToolResult<SendStatus> result;
            PasswordCredentialLease? lease = null;
            try
            {
                CredentialStatus credentialStatus = await credentialVault.GetStatusAsync(
                    payload.AccountId, cancellationToken).ConfigureAwait(false);
                if (!credentialStatus.Configured ||
                    credentialStatus.Kind != CredentialKind.Password)
                {
                    await TransitionTerminalAsync(
                        payload, SendState.Failed, correlationId, cancellationToken).ConfigureAwait(false);
                    return AccountOperationBoundary.Failure<SendStatus>(
                        "credential.not_configured", ErrorCategory.Authentication,
                        "The account credential is not configured.", correlationId);
                }

                lease = await credentialVault.GetPasswordAsync(
                    payload.AccountId, cancellationToken).ConfigureAwait(false);
                SendTransportOutcome outcome = await smtpGateway.SendAsync(
                    profile, lease, message, cancellationToken).ConfigureAwait(false);

                SendLedgerEntry updated = await TransitionTerminalAsync(
                    payload, outcome.State, correlationId, cancellationToken).ConfigureAwait(false);
                result = outcome.State switch
                {
                    SendState.Succeeded =>
                        ToolResult<SendStatus>.Success(ToStatus(updated), correlationId),
                    SendState.Failed => ToolResult<SendStatus>.Failure(
                        outcome.Error ?? DefaultFailure(), correlationId),
                    _ => ToolResult<SendStatus>.Failure(
                        outcome.Error ?? IndeterminateError(), correlationId)
                };
            }
            catch (OperationCanceledException)
            {
                await TryTransitionIndeterminateAsync(payload, correlationId)
                    .ConfigureAwait(false);
                throw;
            }
            catch (MailOperationException exception)
            {
                await TryTransitionAsync(payload, SendState.Failed, correlationId)
                    .ConfigureAwait(false);
                result = ToolResult<SendStatus>.Failure(exception.Error, correlationId);
            }
            catch (Exception)
            {
                await TryTransitionIndeterminateAsync(payload, correlationId)
                    .ConfigureAwait(false);
                result = ToolResult<SendStatus>.Failure(IndeterminateError(), correlationId);
            }
            finally
            {
                lease?.Dispose();
            }

            int bytes = JsonSerializer.SerializeToUtf8Bytes(result).Length;
            PolicyDecision resultDecision = policy.Evaluate(new OperationDescriptor(
                "send_commit",
                RiskLevel.ExternalOrIrreversible,
                Math.Max(1, message.EnvelopeRecipients.Count),
                bytes));
            return resultDecision.Allowed
                ? result
                : ToolResult<SendStatus>.Failure(resultDecision.Error!, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            return ToolResult<SendStatus>.Failure(
                IdempotencyConflict(correlationId), correlationId);
        }
        catch (MailOperationException exception)
        {
            return ToolResult<SendStatus>.Failure(exception.Error, correlationId);
        }
        catch (Exception)
        {
            return AccountOperationBoundary.Failure<SendStatus>(
                "mail.operation_failed", ErrorCategory.Internal,
                "The send commit failed.", correlationId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(message.MimeMessage);
        }
    }

    public async Task<ToolResult<SendStatus>> GetStatusAsync(
        string accountId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        string correlationId = CorrelationId();
        if (!AccountProfileValidator.ValidateId(accountId))
        {
            return AccountOperationBoundary.Failure<SendStatus>(
                "account.invalid_id", ErrorCategory.Validation,
                "The account ID is invalid.", correlationId);
        }

        if (!IsWellFormedIdempotencyKey(idempotencyKey))
        {
            return ValidationFailure<SendStatus>(
                "validation.invalid_idempotency_key",
                "The idempotency key format is invalid.", correlationId);
        }

        try
        {
            SendLedgerEntry? entry = await sendLedger.FindAsync(
                accountId, Hash(idempotencyKey), cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return ValidationFailure<SendStatus>(
                    "send.status_not_found",
                    "No send was recorded for this idempotency key.", correlationId);
            }

            return ToolResult<SendStatus>.Success(ToStatus(entry), correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return AccountOperationBoundary.Failure<SendStatus>(
                "mail.operation_failed", ErrorCategory.Internal,
                "The send status lookup failed.", correlationId);
        }
    }

    private async Task<SendLedgerEntry> TransitionTerminalAsync(
        SendConfirmationPayload payload,
        SendState state,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (state is not (SendState.Succeeded or SendState.Failed or SendState.Indeterminate))
            throw new ArgumentOutOfRangeException(nameof(state));

        return await sendLedger.TransitionAsync(
            payload.AccountId,
            payload.IdempotencyKeyHash,
            state,
            timeProvider.GetUtcNow(),
            correlationId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TryTransitionAsync(
        SendConfirmationPayload payload,
        SendState state,
        string correlationId)
    {
        try
        {
            await sendLedger.TransitionAsync(
                payload.AccountId,
                payload.IdempotencyKeyHash,
                state,
                timeProvider.GetUtcNow(),
                correlationId,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The ledger record keeps its current state; terminal states remain queryable.
        }
    }

    private Task TryTransitionIndeterminateAsync(
        SendConfirmationPayload payload, string correlationId) =>
        TryTransitionAsync(payload, SendState.Indeterminate, correlationId);

    private static bool TryValidateDraft(
        OutgoingMessageDraft? draft, MailSafetyLimits limits, out ToolError? error)
    {
        error = null;
        if (draft is null)
        {
            error = ValidationError(
                "validation.invalid_draft", "The outgoing message draft is required.");
            return false;
        }

        if (Recipients(draft).Count == 0)
        {
            error = ValidationError(
                "validation.missing_recipients",
                "At least one recipient is required.");
            return false;
        }

        foreach (OutgoingMailbox mailbox in Recipients(draft))
        {
            if (mailbox.Address is null ||
                mailbox.Address.Length > 254 ||
                !MailboxAddressPattern.IsMatch(mailbox.Address))
            {
                error = ValidationError(
                    "validation.invalid_recipient",
                    "A recipient address has an invalid format.");
                return false;
            }
        }

        if (draft.From is not null &&
            (draft.From.Address is null || draft.From.Address.Length > 254 ||
             !MailboxAddressPattern.IsMatch(draft.From.Address)))
        {
            error = ValidationError(
                "validation.invalid_recipient", "The sender address has an invalid format.");
            return false;
        }

        if (draft.Subject?.Length > MaxSubjectLength)
        {
            error = ValidationError(
                "validation.subject_too_long",
                $"The subject must be {MaxSubjectLength} characters or fewer.");
            return false;
        }

        if ((draft.TextBody?.Length ?? 0) > limits.MaxBodyCharacters ||
            (draft.HtmlBody?.Length ?? 0) > limits.MaxBodyCharacters)
        {
            error = ValidationError(
                "validation.body_too_long",
                $"A body must be {limits.MaxBodyCharacters} characters or fewer.");
            return false;
        }

        return true;
    }

    private bool TryValidateAttachmentPaths(
        OutgoingMessageDraft draft, out ToolError? error)
    {
        error = null;
        if (draft.AttachmentPaths is null || draft.AttachmentPaths.Count == 0)
            return true;

        foreach (string path in draft.AttachmentPaths)
        {
            string fullPath;
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    throw new FormatException();
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentException or NotSupportedException)
            {
                error = ValidationError(
                    "validation.invalid_attachment",
                    "An attachment path is invalid.");
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = ValidationError(
                    "validation.attachment_not_found",
                    "An attachment file was not found.");
                return false;
            }

            if (!mailFileOptions.UploadRoots.Any(root => IsUnderRoot(fullPath, root)))
            {
                error = ValidationError(
                    "validation.attachment_outside_root",
                    "An attachment is outside the configured upload roots.");
                return false;
            }
        }

        return true;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        string normalizedRoot = Path.GetFullPath(root);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<OutgoingMailbox> Recipients(OutgoingMessageDraft draft)
    {
        List<OutgoingMailbox> recipients = [];
        recipients.AddRange(draft.To ?? []);
        recipients.AddRange(draft.Cc ?? []);
        recipients.AddRange(draft.Bcc ?? []);
        return recipients;
    }

    private static bool IsWellFormedIdempotencyKey(string? idempotencyKey) =>
        idempotencyKey is not null &&
        idempotencyKey.Length <= MaxIdempotencyKeyLength &&
        IdempotencyKeyPattern.IsMatch(idempotencyKey);

    private static IReadOnlyList<string> Display(IReadOnlyList<OutgoingMailbox>? mailboxes) =>
        mailboxes?.Select(mailbox => mailbox.DisplayText).ToArray() ?? [];

    private static string? PreviewText(string? textBody)
    {
        if (string.IsNullOrEmpty(textBody))
            return null;
        return textBody.Length <= TextPreviewLength
            ? textBody
            : textBody[..TextPreviewLength];
    }

    private static string ComputeContentHash(string messageId, OutgoingMessageDraft draft)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("message_id", messageId);
            WriteNullableString(writer, "from", draft.From?.Address);
            WriteStrings(writer, "to", draft.To?.Select(mailbox => mailbox.Address));
            WriteStrings(writer, "cc", draft.Cc?.Select(mailbox => mailbox.Address));
            WriteStrings(writer, "bcc", draft.Bcc?.Select(mailbox => mailbox.Address));
            WriteNullableString(writer, "subject", draft.Subject);
            WriteNullableString(writer, "text_body", draft.TextBody);
            WriteNullableString(writer, "html_body", draft.HtmlBody);
            WriteStrings(writer, "attachment_paths", draft.AttachmentPaths);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        foreach (string value in values)
            writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static SendStatus ToStatus(SendLedgerEntry entry) => new(
        entry.AccountId,
        entry.IdempotencyKeyHash,
        entry.MessageId,
        entry.State,
        entry.PreparedAt,
        entry.AttemptedAt,
        entry.CompletedAt);

    private static ToolError IdempotencyConflict(string correlationId) => new(
        "send.idempotency_conflict",
        ErrorCategory.Conflict,
        "The idempotency key already has a recorded send.",
        false,
        null,
        null);

    private static ToolError IndeterminateError() => new(
        "send.indeterminate",
        ErrorCategory.Transient,
        "The send outcome is unknown; inspect the status before using a new idempotency key.",
        false,
        null,
        null);

    private static ToolError DefaultFailure() => new(
        "send.failed",
        ErrorCategory.Transient,
        "The message was rejected by the SMTP server.",
        false,
        null,
        null);

    private static ToolError ValidationError(string code, string message) =>
        new(code, ErrorCategory.Validation, message, false, null, null);

    private static ToolResult<T> ValidationFailure<T>(
        string code, string message, string correlationId) =>
        ToolResult<T>.Failure(ValidationError(code, message), correlationId);

    private static string CorrelationId() => Guid.NewGuid().ToString("N");
}
