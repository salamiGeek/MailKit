using System.ComponentModel;
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
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var profiles = await store.ListAsync(cancellationToken);
        return ToolResult<IReadOnlyList<AccountProfile>>.Success(profiles, correlationId);
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

        var decision = policy.Evaluate(new OperationDescriptor(
            "account_profile_put", RiskLevel.RecoverableWrite, 1, 4096));
        if (!decision.Allowed)
            return ToolResult<AccountProfile>.Failure(decision.Error!, correlationId);

        await store.PutAsync(profile, cancellationToken);
        return ToolResult<AccountProfile>.Success(profile, correlationId);
    }
}
