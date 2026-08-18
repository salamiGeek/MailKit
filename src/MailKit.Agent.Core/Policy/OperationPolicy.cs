using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Policy;

public sealed class OperationPolicy
{
    public static OperationPolicy Default { get; } = new(PolicyLimits.Default);

    public OperationPolicy(PolicyLimits limits) =>
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));

    public PolicyLimits Limits { get; }

    public PolicyDecision Evaluate(OperationDescriptor operation)
    {
        if (!Enum.IsDefined(operation.Risk))
            return Deny("policy.invalid_risk", "The operation risk level is invalid.");
        if (operation.ItemCount <= 0)
            return Deny("policy.invalid_count", "Item count must be positive.");
        if (operation.ItemCount > Limits.MaxBatchItems)
            return Deny("policy.batch_limit_exceeded", "The operation exceeds the batch limit.");
        if (operation.EstimatedOutputBytes < 0)
            return Deny("policy.output_limit_exceeded", "Output size cannot be negative.");
        if (operation.EstimatedOutputBytes > Limits.MaxStructuredOutputBytes)
            return Deny("policy.output_limit_exceeded", "The operation exceeds the output limit.");

        return new PolicyDecision(
            Allowed: true,
            ConfirmationRequired: operation.Risk is RiskLevel.ExternalOrIrreversible,
            Error: null);
    }

    private static PolicyDecision Deny(string code, string message) =>
        new(false, false, new ToolError(
            code, ErrorCategory.Policy, message, false, null, null));
}
