using System.Text;
using MailKit.Agent.Core.Mail;
using MimeKit;
using MimeKit.Text;

namespace MailKit.Agent.Mail.Mime;

public sealed class MimeContentService
{
    private readonly MimePartLocator locator;

    public MimeContentService()
        : this(new MimePartLocator())
    {
    }

    public MimeContentService(MimePartLocator locator)
    {
        this.locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    public MessageContent Convert(MimeMessage message, BodyMode bodyMode, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (maxCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (!Enum.IsDefined(bodyMode))
            throw new ArgumentOutOfRangeException(nameof(bodyMode));

        IReadOnlyList<MimeEntity> leafParts = locator.GetLeafParts(message);
        string? plainText = message.TextBody;
        string? html = message.HtmlBody;
        string safeText = plainText ?? (html is null ? string.Empty : ConvertHtmlToText(html));
        TruncationResult truncation = Truncate(safeText, maxCharacters);
        TruncationResult? htmlTruncation = bodyMode == BodyMode.Html && html is not null
            ? Truncate(html, maxCharacters)
            : null;

        var summary = new List<MimePartSummary>(leafParts.Count);
        var attachments = new List<AttachmentDescriptor>();
        for (int index = 0; index < leafParts.Count; index++)
        {
            MimeEntity entity = leafParts[index];
            string id = $"part-{index + 1}";
            bool isAttachment = IsAttachment(entity);
            string? fileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;

            summary.Add(new MimePartSummary(
                id,
                entity.ContentType.MimeType,
                entity.ContentDisposition?.Disposition,
                fileName,
                isAttachment));

            if (isAttachment)
            {
                attachments.Add(new AttachmentDescriptor(
                    id,
                    fileName,
                    entity.ContentType.MimeType,
                    GetEncodedSize(entity),
                    string.Equals(entity.ContentDisposition?.Disposition,
                        ContentDisposition.Inline, StringComparison.OrdinalIgnoreCase),
                    entity.ContentId));
            }
        }

        return new MessageContent
        {
            Headers = message.Headers
                .Select(header => new MessageHeader(header.Field, header.Value))
                .ToArray(),
            Text = truncation.Text,
            Html = htmlTruncation?.Text,
            Truncated = truncation.OriginalLength > truncation.ReturnedLength ||
                htmlTruncation is { OriginalLength: var original, ReturnedLength: var returned } && original > returned,
            OriginalCharacterCount = truncation.OriginalLength,
            ReturnedCharacterCount = truncation.ReturnedLength,
            RemoteResourcesLoaded = false,
            Untrusted = true,
            MimeSummary = summary,
            Attachments = attachments,
            ReadStateSupported = false,
            IsRead = null,
            ReadStateUpdated = false
        };
    }

    private static bool IsAttachment(MimeEntity entity)
    {
        if (entity.IsAttachment)
            return true;

        string? fileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static long? GetEncodedSize(MimeEntity entity)
    {
        if (entity is not MimePart { Content.Stream: { CanSeek: true } stream })
            return null;

        try
        {
            return stream.Length;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string ConvertHtmlToText(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader) { IgnoreTruncatedTags = true };
        var text = new StringBuilder();
        int suppressedDepth = 0;

        while (tokenizer.ReadNextToken(out HtmlToken? token))
        {
            if (token is HtmlTagToken tag)
            {
                bool suppressingTag = IsSuppressedTag(tag.Id);
                if (tag.IsEndTag)
                {
                    if (suppressingTag && suppressedDepth > 0)
                        suppressedDepth--;
                    if (suppressedDepth == 0 && IsBlockTag(tag.Id))
                        AppendSpace(text);
                }
                else
                {
                    if (suppressedDepth == 0 && IsBlockTag(tag.Id))
                        AppendSpace(text);
                    if (suppressingTag && !tag.IsEmptyElement)
                        suppressedDepth++;
                    if (suppressedDepth == 0 && tag.Id == HtmlTagId.Image)
                    {
                        HtmlAttribute? alt = tag.Attributes.FirstOrDefault(
                            attribute => attribute.Id == HtmlAttributeId.Alt);
                        if (alt is not null)
                            AppendNormalized(text, alt.Value ?? string.Empty);
                    }
                }
            }
            else if (token.Kind == HtmlTokenKind.Data && suppressedDepth == 0 &&
                token is HtmlDataToken data)
            {
                AppendNormalized(text, data.Data);
            }
        }

        return text.ToString().Trim();
    }

    private static bool IsSuppressedTag(HtmlTagId id) => id is
        HtmlTagId.Script or HtmlTagId.Style or HtmlTagId.Head or HtmlTagId.NoScript;

    private static bool IsBlockTag(HtmlTagId id) => id is
        HtmlTagId.Br or HtmlTagId.Div or HtmlTagId.P or HtmlTagId.LI or
        HtmlTagId.TR or HtmlTagId.H1 or HtmlTagId.H2 or HtmlTagId.H3 or
        HtmlTagId.H4 or HtmlTagId.H5 or HtmlTagId.H6;

    private static void AppendNormalized(StringBuilder builder, string value)
    {
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                AppendSpace(builder);
            }
            else
            {
                builder.Append(character);
            }
        }
    }

    private static void AppendSpace(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != ' ')
            builder.Append(' ');
    }

    private static TruncationResult Truncate(string text, int maxCharacters)
    {
        int originalLength = 0;
        var returned = new StringBuilder(Math.Min(text.Length, maxCharacters));

        foreach (Rune rune in text.EnumerateRunes())
        {
            if (originalLength < maxCharacters)
                returned.Append(rune.ToString());
            originalLength++;
        }

        return new TruncationResult(
            returned.ToString(),
            originalLength,
            Math.Min(originalLength, maxCharacters));
    }

    private readonly record struct TruncationResult(
        string Text,
        int OriginalLength,
        int ReturnedLength);
}
