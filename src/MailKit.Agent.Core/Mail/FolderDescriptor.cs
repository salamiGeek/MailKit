using System.Text.Json.Serialization;

namespace MailKit.Agent.Core.Mail;

public sealed record FolderDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_selectable")] bool IsSelectable,
    [property: JsonPropertyName("attributes")] IReadOnlyList<string> Attributes,
    [property: JsonPropertyName("special_use")] string? SpecialUse);
