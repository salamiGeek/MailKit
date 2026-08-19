using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using ModelContextProtocol.Server;

namespace MailKit.Agent.Mcp.Tools;

public sealed record AccountIdRequest(
    [property: JsonPropertyName("account_id")] string AccountId);

[McpServerToolType]
public sealed class AccountTools
{
    [McpServerTool(Name = "account_list", UseStructuredContent = true)]
    [Description("Lists configured non-secret email account profiles.")]
    public static async Task<ToolResult<IReadOnlyList<AccountProfile>>> ListAsync(
        IAccountProfileStore store,
        OperationPolicy policy,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        return await ExecuteStoreOperationAsync(correlationId, async () =>
        {
            var profiles = await store.ListAsync(cancellationToken);
            var success = ToolResult<IReadOnlyList<AccountProfile>>.Success(profiles, correlationId);
            var decision = policy.Evaluate(new OperationDescriptor(
                "account_list",
                RiskLevel.ReadOnly,
                Math.Max(1, profiles.Count),
                JsonSerializer.SerializeToUtf8Bytes(success).Length));
            return decision.Allowed
                ? success
                : ToolResult<IReadOnlyList<AccountProfile>>.Failure(
                    decision.Error!, correlationId);
        });
    }

    [McpServerTool(Name = "account_profile_put", UseStructuredContent = true)]
    [Description("Creates or replaces a non-secret email account profile. Never accepts passwords or tokens.")]
    public static async Task<ToolResult<AccountProfile>> PutAsync(
        [Description("Non-secret account endpoints and authentication type.")] AccountProfile profile,
        IAccountProfileStore store,
        OperationPolicy policy,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        return await ExecuteStoreOperationAsync(correlationId, async () =>
        {
            var validation = string.IsNullOrEmpty(profile.Id)
                ? ["id: invalid format"]
                : AccountProfileValidator.Validate(profile);
            if (validation.Count > 0)
            {
                return ToolResult<AccountProfile>.Failure(
                    new ToolError(
                        validation.Any(item => item.EndsWith("TLS is required", StringComparison.Ordinal))
                            ? "account.tls_required"
                            : "account.invalid_profile",
                        ErrorCategory.Validation,
                        "The account profile is invalid.",
                        false,
                        null,
                        validation.Select((message, index) => (index, message))
                            .ToDictionary(item => $"issue_{item.index + 1}", item => item.message)),
                    correlationId);
            }

            var success = ToolResult<AccountProfile>.Success(profile, correlationId);
            var decision = policy.Evaluate(new OperationDescriptor(
                "account_profile_put",
                RiskLevel.RecoverableWrite,
                1,
                JsonSerializer.SerializeToUtf8Bytes(success).Length));
            if (!decision.Allowed)
                return ToolResult<AccountProfile>.Failure(decision.Error!, correlationId);

            await store.PutAsync(profile, cancellationToken);
            return success;
        });
    }

    [McpServerTool(Name = "account_credential_status", UseStructuredContent = true)]
    [Description(
        "Reports whether a stored credential exists for one account. Never returns or accepts password values.")]
    public static async Task<ToolResult<CredentialStatus>> CredentialStatusAsync(
        [Description("Account ID whose stored credential is inspected.")] AccountIdRequest request,
        IAccountCredentialVault vault,
        CancellationToken cancellationToken)
    {
        var accountId = request.AccountId;
        var correlationId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return ToolResult<CredentialStatus>.Failure(
                new ToolError(
                    "account.invalid_id",
                    ErrorCategory.Validation,
                    "The account ID is invalid.",
                    false,
                    null,
                    null),
                correlationId);
        }

        try
        {
            CredentialStatus status = await vault.GetStatusAsync(accountId, cancellationToken);
            return ToolResult<CredentialStatus>.Success(status, correlationId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ToolResult<CredentialStatus>.Failure(
                new ToolError(
                    "credential.status_failed",
                    ErrorCategory.Internal,
                    "The credential status lookup failed.",
                    false,
                    null,
                    null),
                correlationId);
        }
    }

    private static async Task<ToolResult<T>> ExecuteStoreOperationAsync<T>(
        string correlationId,
        Func<Task<ToolResult<T>>> operation)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ToolResult<T>.Failure(
                new ToolError(
                    "account.store_failure",
                    ErrorCategory.Internal,
                    "The account profile store operation failed.",
                    false,
                    null,
                    null),
                correlationId);
        }
    }
}
