using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Policy;

public sealed record OperationDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("risk")] RiskLevel Risk,
    [property: JsonPropertyName("item_count")] int ItemCount,
    [property: JsonPropertyName("estimated_output_bytes")] int EstimatedOutputBytes);
