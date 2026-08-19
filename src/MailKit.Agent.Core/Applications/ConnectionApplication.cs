using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Applications;

public sealed class ConnectionApplication
{
    private static readonly string[] SupportedProtocols = ["imap", "pop3", "smtp"];
    private readonly IAccountProfileStore accountStore;
    private readonly AccountOperationBoundary boundary;
    private readonly IProtocolConnectionTester tester;
    private readonly OperationPolicy policy;

    public ConnectionApplication(
        IAccountProfileStore accountStore,
        IAccountCredentialVault credentialVault,
        IProtocolConnectionTester tester,
        OperationPolicy policy)
    {
        this.accountStore = accountStore ?? throw new ArgumentNullException(nameof(accountStore));
        boundary = new AccountOperationBoundary(accountStore, credentialVault, policy);
        this.tester = tester ?? throw new ArgumentNullException(nameof(tester));
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    public async Task<ToolResult<IReadOnlyList<ProtocolConnectionResult>>> TestAsync(
        string accountId,
        IReadOnlyList<string>? protocols,
        CancellationToken cancellationToken)
    {
        string correlationId = Guid.NewGuid().ToString("N");
        if (!AccountProfileValidator.ValidateId(accountId))
        {
            return AccountOperationBoundary.Failure<IReadOnlyList<ProtocolConnectionResult>>(
                "account.invalid_id", ErrorCategory.Validation,
                "The account ID is invalid.", correlationId);
        }

        AccountProfile? profile;
        try
        {
            profile = await accountStore.GetAsync(accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return AccountOperationBoundary.Failure<IReadOnlyList<ProtocolConnectionResult>>(
                "mail.operation_failed", ErrorCategory.Internal,
                "The mail operation failed.", correlationId);
        }

        if (profile is null)
        {
            return AccountOperationBoundary.Failure<IReadOnlyList<ProtocolConnectionResult>>(
                "account.not_found", ErrorCategory.Validation,
                "The account was not found.", correlationId);
        }
        if (!string.Equals(profile.Id, accountId, StringComparison.Ordinal) ||
            AccountProfileValidator.Validate(profile).Count != 0)
        {
            return AccountOperationBoundary.Failure<IReadOnlyList<ProtocolConnectionResult>>(
                "account.invalid_profile", ErrorCategory.Validation,
                "The stored account profile is invalid.", correlationId);
        }

        string[] requested = protocols is null || protocols.Count == 0
            ? SupportedProtocols.Where(protocol =>
                AccountOperationBoundary.GetEndpoint(profile, protocol) is not null).ToArray()
            : protocols
                .Select(protocol => protocol?.ToLowerInvariant() ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        PolicyDecision initialDecision = policy.Evaluate(new OperationDescriptor(
            "account_connection_test", RiskLevel.ReadOnly, Math.Max(1, requested.Length), 0));
        if (!initialDecision.Allowed)
        {
            return ToolResult<IReadOnlyList<ProtocolConnectionResult>>.Failure(
                initialDecision.Error!, correlationId);
        }

        var results = new List<ProtocolConnectionResult>(requested.Length);
        foreach (string protocol in requested)
        {
            if (!SupportedProtocols.Contains(protocol, StringComparer.Ordinal))
            {
                results.Add(Failed(protocol, new ToolError(
                    "connection.protocol_error", ErrorCategory.Capability,
                    "The requested mail protocol is not supported.", false, null, null)));
                continue;
            }

            ToolResult<ProtocolConnectionResult> result = await boundary.ExecuteAsync(
                accountId,
                protocol,
                "account_connection_test",
                RiskLevel.ReadOnly,
                1,
                (resolvedProfile, credential) => tester.TestAsync(
                    protocol, resolvedProfile, credential, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            results.Add(result.Ok ? result.Data! : Failed(protocol, result.Error!));
        }

        IReadOnlyList<ProtocolConnectionResult> output = results;
        ToolResult<IReadOnlyList<ProtocolConnectionResult>> success =
            ToolResult<IReadOnlyList<ProtocolConnectionResult>>.Success(output, correlationId);
        int bytes = JsonSerializer.SerializeToUtf8Bytes(success).Length;
        PolicyDecision outputDecision = policy.Evaluate(new OperationDescriptor(
            "account_connection_test", RiskLevel.ReadOnly, Math.Max(1, results.Count), bytes));
        return outputDecision.Allowed
            ? success
            : ToolResult<IReadOnlyList<ProtocolConnectionResult>>.Failure(
                outputDecision.Error!, correlationId);
    }

    private static ProtocolConnectionResult Failed(string protocol, ToolError error) =>
        new(protocol, false, false, false, Array.Empty<string>(), error);
}
