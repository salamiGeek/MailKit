using System.ComponentModel;
using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using ModelContextProtocol.Server;

namespace MailKit.Agent.Mcp.Tools;

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
