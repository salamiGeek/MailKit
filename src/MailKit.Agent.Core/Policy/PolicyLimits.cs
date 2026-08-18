using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Policy;

public sealed record PolicyLimits(
    [property: JsonPropertyName("max_batch_items")] int MaxBatchItems,
    [property: JsonPropertyName("max_structured_output_bytes")] int MaxStructuredOutputBytes)
{
    public static PolicyLimits Default { get; } = new(500, 1_048_576);
}
