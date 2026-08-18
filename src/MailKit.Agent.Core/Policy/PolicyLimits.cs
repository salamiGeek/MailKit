namespace MailKit.Agent.Core.Policy;

public sealed record PolicyLimits(int MaxBatchItems, int MaxStructuredOutputBytes)
{
    public static PolicyLimits Default { get; } = new(500, 1_048_576);
}
