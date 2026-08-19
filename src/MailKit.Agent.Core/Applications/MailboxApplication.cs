using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Paging;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Applications;

public sealed class MailboxApplication
{
    private static readonly TimeSpan CursorLifetime = TimeSpan.FromMinutes(10);
    private readonly AccountOperationBoundary boundary;
    private readonly IImapGateway imapGateway;
    private readonly IPop3Gateway pop3Gateway;
    private readonly ICursorCodec cursorCodec;
    private readonly MailSafetyLimits safetyLimits;
    private readonly TimeProvider timeProvider;

    public MailboxApplication(
        IAccountProfileStore accountStore,
        IAccountCredentialVault credentialVault,
        IImapGateway imapGateway,
        IPop3Gateway pop3Gateway,
        ICursorCodec cursorCodec,
        OperationPolicy policy,
        MailSafetyLimits safetyLimits,
        TimeProvider? timeProvider = null)
    {
        boundary = new AccountOperationBoundary(accountStore, credentialVault, policy);
        this.imapGateway = imapGateway ?? throw new ArgumentNullException(nameof(imapGateway));
        this.pop3Gateway = pop3Gateway ?? throw new ArgumentNullException(nameof(pop3Gateway));
        this.cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
        this.safetyLimits = safetyLimits ?? throw new ArgumentNullException(nameof(safetyLimits));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (safetyLimits.MaxPageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(safetyLimits));
    }

    public Task<ToolResult<IReadOnlyList<FolderDescriptor>>> ListFoldersAsync(
        string accountId,
        CancellationToken cancellationToken) =>
        boundary.ExecuteAsync(
            accountId, "imap", "folder_list", RiskLevel.ReadOnly, 1,
            (profile, credential) => imapGateway.ListFoldersAsync(
                profile, credential, cancellationToken),
            cancellationToken,
            folders => Math.Max(1, folders.Count));

    public Task<ToolResult<MessagePage>> ListImapAsync(
        string accountId,
        string folderId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return Task.FromResult(InvalidFolderResult());

        return ListPageAsync(
            accountId,
            RequireFolderScope("imap:list", folderId),
            "imap",
            "message_list",
            pageSize,
            cursor,
            (profile, credential, offset) => imapGateway.ListMessagesAsync(
                profile, credential, folderId, offset, pageSize, cancellationToken),
            cancellationToken);
    }

    public Task<ToolResult<MessagePage>> SearchImapAsync(
        string accountId,
        string folderId,
        MessageSearchCriteria criteria,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folderId))
            return Task.FromResult(InvalidFolderResult());
        if (criteria is null)
        {
            return Task.FromResult(ToolResult<MessagePage>.Failure(
                ValidationError(
                    "validation.invalid_search_criteria",
                    "Search criteria are required."),
                CorrelationId()));
        }
        string scope = $"{RequireFolderScope("imap:search", folderId)}:{HashCriteria(criteria)}";
        return ListPageAsync(
            accountId,
            scope,
            "imap",
            "message_search",
            pageSize,
            cursor,
            (profile, credential, offset) => imapGateway.SearchAsync(
                profile, credential, folderId, criteria, offset, pageSize, cancellationToken),
            cancellationToken);
    }

    public Task<ToolResult<MessagePage>> ListPop3Async(
        string accountId,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken) =>
        ListPageAsync(
            accountId,
            "pop3:list",
            "pop3",
            "pop3_message_list",
            pageSize,
            cursor,
            (profile, credential, offset) => pop3Gateway.ListMessagesAsync(
                profile, credential, offset, pageSize, cancellationToken),
            cancellationToken);

    public Task<ToolResult<MessageContent>> ReadAsync(
        MessageReference reference,
        bool markAsRead,
        BodyMode bodyMode,
        CancellationToken cancellationToken)
    {
        if (!TryValidateReference(reference, out ToolError? error))
            return Task.FromResult(ToolResult<MessageContent>.Failure(error!, CorrelationId()));
        if (!Enum.IsDefined(bodyMode))
            return Task.FromResult(ToolResult<MessageContent>.Failure(
                ValidationError("validation.invalid_body_mode", "The body mode is invalid."),
                CorrelationId()));
        if (reference.Protocol == MailProtocol.Pop3 && markAsRead)
            return Task.FromResult(ToolResult<MessageContent>.Failure(
                new ToolError(
                    "pop3.read_state_unsupported", ErrorCategory.Capability,
                    "POP3 does not support server read state.", false, null, null),
                CorrelationId()));

        string protocol = ProtocolName(reference.Protocol);
        return boundary.ExecuteAsync(
            reference.AccountId,
            protocol,
            reference.Protocol == MailProtocol.Imap ? "message_read" : "pop3_message_read",
            markAsRead ? RiskLevel.RecoverableWrite : RiskLevel.ReadOnly,
            1,
            (profile, credential) => reference.Protocol == MailProtocol.Imap
                ? imapGateway.ReadAsync(
                    profile, credential, reference, markAsRead, bodyMode, cancellationToken)
                : pop3Gateway.ReadAsync(
                    profile, credential, reference, bodyMode, cancellationToken),
            cancellationToken);
    }

    public Task<ToolResult<int>> MarkReadAsync(
        IReadOnlyList<MessageReference> references,
        bool isRead,
        CancellationToken cancellationToken)
    {
        if (references is null)
        {
            return Task.FromResult(ToolResult<int>.Failure(
                ValidationError(
                    "validation.invalid_references",
                    "Message references are required."),
                CorrelationId()));
        }
        if (references.Count == 0)
        {
            return Task.FromResult(ToolResult<int>.Failure(
                ValidationError("validation.empty_references", "At least one message reference is required."),
                CorrelationId()));
        }

        MessageReference first = references[0];
        if (!TryValidateReference(first, out ToolError? error) ||
            first.Protocol != MailProtocol.Imap ||
            references.Any(reference => !TryValidateReference(reference, out _) ||
                reference.Protocol != MailProtocol.Imap ||
                !string.Equals(reference.AccountId, first.AccountId, StringComparison.Ordinal)))
        {
            return Task.FromResult(ToolResult<int>.Failure(
                error ?? ReferenceConflict(), CorrelationId()));
        }

        return boundary.ExecuteAsync(
            first.AccountId,
            "imap",
            "message_mark_read",
            RiskLevel.RecoverableWrite,
            references.Count,
            (profile, credential) => imapGateway.MarkReadAsync(
                profile, credential, references, isRead, cancellationToken),
            cancellationToken);
    }

    private async Task<ToolResult<MessagePage>> ListPageAsync(
        string accountId,
        string scope,
        string protocol,
        string operationName,
        int pageSize,
        string? cursor,
        Func<AccountProfile, PasswordCredentialLease, int, Task<MessagePage>> operation,
        CancellationToken cancellationToken)
    {
        if (pageSize < 1 || pageSize > safetyLimits.MaxPageSize)
        {
            return ToolResult<MessagePage>.Failure(
                ValidationError(
                    "validation.invalid_page_size",
                    $"Page size must be between 1 and {safetyLimits.MaxPageSize}."),
                CorrelationId());
        }

        if (!TryDecodeOffset(cursor, accountId, scope, pageSize, out int offset))
        {
            return ToolResult<MessagePage>.Failure(InvalidCursorError(), CorrelationId());
        }

        return await boundary.ExecuteAsync(
            accountId,
            protocol,
            operationName,
            RiskLevel.ReadOnly,
            pageSize,
            async (profile, credential) =>
            {
                MessagePage gatewayPage = await operation(profile, credential, offset)
                    .ConfigureAwait(false);
                string? nextCursor = gatewayPage.NextOffset is int nextOffset
                    ? EncodeCursor(accountId, scope, nextOffset, pageSize)
                    : null;
                return new MessagePage(gatewayPage.Messages, null) { NextCursor = nextCursor };
            },
            cancellationToken,
            page => Math.Max(1, page.Messages.Count)).ConfigureAwait(false);
    }

    private bool TryDecodeOffset(
        string? cursor,
        string accountId,
        string scope,
        int pageSize,
        out int offset)
    {
        offset = 0;
        if (cursor is null)
            return true;

        try
        {
            CursorPayload payload = cursorCodec.Decode(cursor);
            return payload.ExpiresAt > timeProvider.GetUtcNow() &&
                string.Equals(payload.AccountId, accountId, StringComparison.Ordinal) &&
                string.Equals(payload.Scope, scope, StringComparison.Ordinal) &&
                payload.PageSize == pageSize &&
                int.TryParse(
                    payload.Position,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out offset) &&
                offset >= 0;
        }
        catch (InvalidCursorException)
        {
            return false;
        }
    }

    private string EncodeCursor(string accountId, string scope, int offset, int pageSize)
    {
        if (offset < 0)
            throw new MailOperationException(InvalidCursorError());

        return cursorCodec.Encode(new CursorPayload(
            accountId,
            scope,
            offset.ToString(CultureInfo.InvariantCulture),
            timeProvider.GetUtcNow().Add(CursorLifetime))
        {
            PageSize = pageSize
        });
    }

    private static string HashCriteria(MessageSearchCriteria criteria)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteNullableString(writer, "text", criteria.Text);
            WriteNullableString(writer, "from", criteria.From);
            WriteNullableString(writer, "to", criteria.To);
            WriteNullableString(writer, "subject", criteria.Subject);
            WriteNullableDate(writer, "since", criteria.Since);
            WriteNullableDate(writer, "before", criteria.Before);
            if (criteria.Unread.HasValue)
                writer.WriteBoolean("unread", criteria.Unread.Value);
            else
                writer.WriteNull("unread");
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteNullableDate(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value.HasValue)
            writer.WriteString(name, value.Value.ToString("O", CultureInfo.InvariantCulture));
        else
            writer.WriteNull(name);
    }

    private static string RequireFolderScope(string prefix, string folderId)
    {
        return $"{prefix}:{folderId}";
    }

    private static ToolResult<MessagePage> InvalidFolderResult() =>
        ToolResult<MessagePage>.Failure(
            ValidationError(
                "validation.invalid_folder_id",
                "The folder ID is required."),
            CorrelationId());

    private static bool TryValidateReference(MessageReference? reference, out ToolError? error)
    {
        error = null;
        if (reference is null ||
            !AccountProfileValidator.ValidateId(reference.AccountId) ||
            reference.Protocol == MailProtocol.Imap &&
            (string.IsNullOrWhiteSpace(reference.FolderId) ||
             reference.UidValidity is null or 0 || reference.Uid is null or 0 ||
             reference.Uidl is not null) ||
            reference.Protocol == MailProtocol.Pop3 &&
            (string.IsNullOrWhiteSpace(reference.Uidl) || reference.FolderId is not null ||
             reference.UidValidity is not null || reference.Uid is not null) ||
            !Enum.IsDefined(reference.Protocol))
        {
            error = ReferenceConflict();
            return false;
        }

        return true;
    }

    private static string ProtocolName(MailProtocol protocol) =>
        protocol == MailProtocol.Imap ? "imap" : "pop3";

    private static ToolError ReferenceConflict() => new(
        "message.reference_conflict",
        ErrorCategory.Conflict,
        "The message reference is invalid or does not match the requested account.",
        false,
        null,
        null);

    private static ToolError InvalidCursorError() => ValidationError(
        "paging.invalid_cursor", "The cursor is invalid or expired.");

    private static ToolError ValidationError(string code, string message) =>
        new(code, ErrorCategory.Validation, message, false, null, null);

    private static string CorrelationId() => Guid.NewGuid().ToString("N");
}

internal sealed class AccountOperationBoundary
{
    private readonly IAccountProfileStore accountStore;
    private readonly IAccountCredentialVault credentialVault;
    private readonly OperationPolicy policy;

    public AccountOperationBoundary(
        IAccountProfileStore accountStore,
        IAccountCredentialVault credentialVault,
        OperationPolicy policy)
    {
        this.accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        this.credentialVault = credentialVault ?? throw new ArgumentNullException(nameof(credentialVault));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<ToolResult<T>> ExecuteAsync<T>(
        string accountId,
        string protocol,
        string operationName,
        RiskLevel risk,
        int itemCount,
        Func<AccountProfile, PasswordCredentialLease, Task<T>> operation,
        CancellationToken cancellationToken,
        Func<T, int>? actualItemCount = null)
    {
        string correlationId = Guid.NewGuid().ToString("N");
        PolicyDecision initialDecision = policy.Evaluate(new OperationDescriptor(
            operationName, risk, itemCount, 0));
        if (!initialDecision.Allowed)
            return ToolResult<T>.Failure(initialDecision.Error!, correlationId);

        if (!AccountProfileValidator.ValidateId(accountId))
            return Failure<T>("account.invalid_id", ErrorCategory.Validation,
                "The account ID is invalid.", correlationId);

        try
        {
            AccountProfile? profile = await accountStore.GetAsync(accountId, cancellationToken)
                .ConfigureAwait(false);
            if (profile is null)
                return Failure<T>("account.not_found", ErrorCategory.Validation,
                    "The account was not found.", correlationId);
            if (!string.Equals(profile.Id, accountId, StringComparison.Ordinal) ||
                AccountProfileValidator.Validate(profile).Count != 0)
            {
                return Failure<T>("account.invalid_profile", ErrorCategory.Validation,
                    "The stored account profile is invalid.", correlationId);
            }

            if (GetEndpoint(profile, protocol) is null)
            {
                return Failure<T>(
                    $"{protocol}.not_configured",
                    ErrorCategory.Capability,
                    $"{protocol.ToUpperInvariant()} is not configured for this account.",
                    correlationId,
                    new Dictionary<string, string> { ["protocol"] = protocol });
            }

            CredentialStatus status = await credentialVault.GetStatusAsync(
                accountId, cancellationToken).ConfigureAwait(false);
            if (!status.Configured || status.Kind != CredentialKind.Password)
            {
                return Failure<T>(
                    "credential.not_configured",
                    ErrorCategory.Authentication,
                    "The account credential is not configured.",
                    correlationId);
            }

            PasswordCredentialLease? credential = null;
            try
            {
                credential = await credentialVault.GetPasswordAsync(
                    accountId, cancellationToken).ConfigureAwait(false);
                T data = await operation(profile, credential).ConfigureAwait(false);
                ToolResult<T> success = ToolResult<T>.Success(data, correlationId);
                int bytes = JsonSerializer.SerializeToUtf8Bytes(success).Length;
                int resultItemCount = actualItemCount?.Invoke(data) ?? itemCount;
                PolicyDecision resultDecision = policy.Evaluate(new OperationDescriptor(
                    operationName, risk, resultItemCount, bytes));
                return resultDecision.Allowed
                    ? success
                    : ToolResult<T>.Failure(resultDecision.Error!, correlationId);
            }
            finally
            {
                credential?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MailOperationException exception)
        {
            return ToolResult<T>.Failure(exception.Error, correlationId);
        }
        catch (Exception)
        {
            return Failure<T>(
                "mail.operation_failed",
                ErrorCategory.Internal,
                "The mail operation failed.",
                correlationId);
        }
    }

    internal static EndpointSettings? GetEndpoint(AccountProfile profile, string protocol) =>
        protocol switch
        {
            "imap" => profile.Imap,
            "pop3" => profile.Pop3,
            "smtp" => profile.Smtp,
            _ => null
        };

    internal static ToolResult<T> Failure<T>(
        string code,
        ErrorCategory category,
        string message,
        string correlationId,
        IReadOnlyDictionary<string, string>? details = null) =>
        ToolResult<T>.Failure(
            new ToolError(code, category, message, false, null, details), correlationId);
}
