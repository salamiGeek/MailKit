using MimeKit;

namespace MailKit.Agent.Mail.Mime;

public sealed class MimePartLocator
{
    public IReadOnlyList<MimeEntity> GetLeafParts(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parts = new List<MimeEntity>();
        if (message.Body is not null)
            AddLeafParts(message.Body, parts);

        return parts;
    }

    public MimeEntity? Find(MimeMessage message, string partId)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (string.IsNullOrWhiteSpace(partId) ||
            !partId.StartsWith("part-", StringComparison.Ordinal) ||
            !int.TryParse(partId.AsSpan(5), out int ordinal) || ordinal < 1)
        {
            return null;
        }

        IReadOnlyList<MimeEntity> parts = GetLeafParts(message);
        return ordinal <= parts.Count ? parts[ordinal - 1] : null;
    }

    private static void AddLeafParts(MimeEntity entity, ICollection<MimeEntity> parts)
    {
        if (entity is Multipart multipart)
        {
            foreach (MimeEntity child in multipart)
                AddLeafParts(child, parts);
            return;
        }

        parts.Add(entity);
    }
}
