namespace MailKit.Agent.Core.Policy;

public sealed record OperationDescriptor(
    string Name,
    RiskLevel Risk,
    int ItemCount,
    int EstimatedOutputBytes);
