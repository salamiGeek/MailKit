using System.Text;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Mail.Mime;
using MimeKit;

namespace MailKit.Agent.Mail.Tests.Mime;

public sealed class MimeContentServiceTests
{
    [Test]
    public void ConvertsHtmlToSafeTextWithoutLoadingRemoteResourcesAndUsesTraversalIds()
    {
        var message = new MimeMessage();
        message.Headers.Add("X-Untrusted", "header-value");
        message.Body = new Multipart("mixed")
        {
            new TextPart("html")
            {
                Text = "<html><body>Visible text<script>hidden()</script>" +
                    "<img src=\"https://tracker.example/pixel.png\"></body></html>"
            },
            CreateAttachment("../../escape.exe", new byte[] { 1, 2, 3 })
        };

        var service = new MimeContentService();

        MessageContent content = service.Convert(message, BodyMode.SafeText, maxCharacters: 4096);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Does.Contain("Visible text"));
            Assert.That(content.Text, Does.Not.Contain("<script"));
            Assert.That(content.Text, Does.Not.Contain("hidden()"));
            Assert.That(content.Text, Does.Not.Contain("tracker.example"));
            Assert.That(content.Html, Is.Null);
            Assert.That(content.RemoteResourcesLoaded, Is.False);
            Assert.That(content.Untrusted, Is.True);
            Assert.That(content.Attachments.Single().Id, Is.EqualTo("part-2"));
            Assert.That(content.Attachments.Single().FileName, Is.EqualTo("../../escape.exe"));
            Assert.That(content.MimeSummary.Select(part => part.Id),
                Is.EqualTo(new[] { "part-1", "part-2" }));
            Assert.That(content.Headers, Has.Some.Property("Name").EqualTo("X-Untrusted"));
        });
    }

    [Test]
    public void PrefersPlainTextAndReturnsHtmlOnlyWhenRequested()
    {
        var message = new MimeMessage
        {
            Body = new MultipartAlternative
            {
                new TextPart("plain") { Text = "Preferred plain text" },
                new TextPart("html") { Text = "<p>Different HTML text</p>" }
            }
        };
        var service = new MimeContentService();

        MessageContent safeText = service.Convert(message, BodyMode.SafeText, 4096);
        MessageContent withHtml = service.Convert(message, BodyMode.Html, 4096);

        Assert.Multiple(() =>
        {
            Assert.That(safeText.Text, Is.EqualTo("Preferred plain text"));
            Assert.That(safeText.Html, Is.Null);
            Assert.That(withHtml.Text, Is.EqualTo("Preferred plain text"));
            Assert.That(withHtml.Html, Is.EqualTo("<p>Different HTML text</p>"));
            Assert.That(withHtml.RemoteResourcesLoaded, Is.False);
        });
    }

    [Test]
    public void TruncatesAtUnicodeScalarBoundaryAndReportsScalarLengths()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("plain") { Text = "A\U0001F600BC" }
        };
        var service = new MimeContentService();

        MessageContent content = service.Convert(message, BodyMode.SafeText, maxCharacters: 2);

        Assert.Multiple(() =>
        {
            Assert.That(content.Text, Is.EqualTo("A\U0001F600"));
            Assert.That(content.Text[^1], Is.EqualTo('\uDE00'));
            Assert.That(content.Truncated, Is.True);
            Assert.That(content.OriginalCharacterCount, Is.EqualTo(4));
            Assert.That(content.ReturnedCharacterCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void HtmlModeAlsoBoundsUntrustedHtmlAtUnicodeScalarBoundary()
    {
        var message = new MimeMessage
        {
            Body = new TextPart("html") { Text = "<p>A\U0001F600BC</p>" }
        };
        var service = new MimeContentService();

        MessageContent content = service.Convert(message, BodyMode.Html, maxCharacters: 5);

        Assert.Multiple(() =>
        {
            Assert.That(content.Html, Is.EqualTo("<p>A\U0001F600"));
            Assert.That(content.Html![^1], Is.EqualTo('\uDE00'));
            Assert.That(content.Truncated, Is.True);
        });
    }

    [Test]
    public void RejectsNonPositiveCharacterLimit()
    {
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "body" } };
        var service = new MimeContentService();

        Assert.That(() => service.Convert(message, BodyMode.SafeText, 0),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void LocatorFindsLeafByTraversalIdAndNotByUntrustedFilename()
    {
        var attachment = CreateAttachment("part-1", new byte[] { 1, 2, 3 });
        var message = new MimeMessage
        {
            Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "body" },
                attachment
            }
        };
        var locator = new MimePartLocator();

        Assert.Multiple(() =>
        {
            Assert.That(locator.Find(message, "part-2"), Is.SameAs(attachment));
            Assert.That(locator.Find(message, "part-1"), Is.Not.SameAs(attachment));
            Assert.That(locator.Find(message, "part-1.exe"), Is.Null);
            Assert.That(locator.Find(message, "part-3"), Is.Null);
            Assert.That(locator.Find(message, " "), Is.Null);
        });
    }

    private static MimePart CreateAttachment(string fileName, byte[] bytes) =>
        new("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(bytes, writable: false)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName
        };
}
