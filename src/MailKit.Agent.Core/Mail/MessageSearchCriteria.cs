using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record MessageSearchCriteria
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }

    [JsonPropertyName("subject")]
    public string? Subject { get; init; }

    [JsonPropertyName("since")]
    public DateTime? Since { get; init; }

    [JsonPropertyName("before")]
    public DateTime? Before { get; init; }

    [JsonPropertyName("unread")]
    public bool? Unread { get; init; }
}
