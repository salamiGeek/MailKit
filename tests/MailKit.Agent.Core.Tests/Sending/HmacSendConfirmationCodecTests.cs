using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Agent.Core.Sending;

namespace MailKit.Agent.Core.Tests.Sending;

public class HmacSendConfirmationCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    private static SendConfirmationPayload Payload(
        DateTimeOffset? expiresAt = null,
        string accountId = "personal",
        string preparationId = "prep-1",
        string sessionId = "session-a") =>
        new(
            preparationId,
            accountId,
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
            sessionId,
            expiresAt ?? Now.AddMinutes(10));

    [Test]
    public void ConfirmationRoundTrips()
    {
        var time = new FakeTimeProvider(Now);
        var codec = new HmacSendConfirmationCodec(Key, time);
        var payload = Payload();

        var decoded = codec.Decode(codec.Encode(payload));

        Assert.That(decoded, Is.EqualTo(payload));
    }

    [Test]
    public void TokenBindsOnlyIdentityAndHashesNeverMessageContent()
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(Payload());
        var payloadSegment = token[..token.IndexOf('.')];
        using var document = JsonDocument.Parse(Base64UrlDecode(payloadSegment));

        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name);

        Assert.That(propertyNames, Is.EquivalentTo(new[]
        {
            "preparation_id",
            "account_id",
            "content_hash",
            "idempotency_key_hash",
            "session_id",
            "expires_at"
        }));
    }

    [Test]
    public void TokenNeverContainsRecipientsSubjectBodyOrAttachmentNames()
    {
        const string secretSubject = "Quarterly-Launch-Plan";
        const string secretRecipient = "hidden-recipient@example.test";
        const string secretAttachment = "secret-payroll.xlsx";
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(Payload());

        Assert.Multiple(() =>
        {
            Assert.That(token, Does.Not.Contain(secretSubject));
            Assert.That(token, Does.Not.Contain(secretRecipient));
            Assert.That(token, Does.Not.Contain(secretAttachment));
            Assert.That(token, Does.Not.Contain(Base64UrlEncode(Encoding.UTF8.GetBytes(secretSubject))));
            Assert.That(token, Does.Not.Contain(Base64UrlEncode(Encoding.UTF8.GetBytes(secretRecipient))));
            Assert.That(token, Does.Not.Contain(Base64UrlEncode(Encoding.UTF8.GetBytes(secretAttachment))));
        });
    }

    [Test]
    public void DecodeRejectsTampering()
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(Payload());
        var separator = token.IndexOf('.');
        var replacement = token[separator + 1] == 'A' ? 'B' : 'A';
        var tampered = token[..(separator + 1)] + replacement + token[(separator + 2)..];

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(tampered));
    }

    [TestCase('-', '+')]
    [TestCase('_', '/')]
    public void DecodeRejectsNonUrlSafeCharacters(char urlSafeCharacter, char standardBase64Character)
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(Payload());
        var separator = token.IndexOf('.');
        var signature = token[(separator + 1)..];
        Assert.That(signature, Does.Contain(urlSafeCharacter.ToString()), "Test token must exercise the replacement.");
        var nonUrlSafe = token[..(separator + 1)] + signature.Replace(urlSafeCharacter, standardBase64Character);

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(nonUrlSafe));
    }

    [Test]
    public void DecodeRejectsBase64UrlPadding()
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(Payload());

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(token + "="));
    }

    [Test]
    public void DecodeRejectsNonCanonicalTailBits()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(Payload());
        var lastIndex = alphabet.IndexOf(token[^1]);
        Assert.That(lastIndex % 4, Is.Zero, "A 32-byte signature must end with four data bits and two zero bits.");
        var nonCanonical = token[..^1] + alphabet[lastIndex + 1];

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(nonCanonical));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void EncodeRequiresFutureExpiry(int minuteOffset)
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));

        Assert.Throws<ArgumentException>(() => codec.Encode(Payload(Now.AddMinutes(minuteOffset))));
    }

    [Test]
    public void DecodeRejectsExpiredConfirmation()
    {
        var time = new FakeTimeProvider(Now);
        var codec = new HmacSendConfirmationCodec(Key, time);
        var token = codec.Encode(Payload(Now.AddMinutes(10)));
        time.SetUtcNow(Now.AddMinutes(10));

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(token));
    }

    [TestCase("")]
    [TestCase("payload")]
    [TestCase("payload.signature.extra")]
    [TestCase("a.invalid!")]
    public void DecodeRejectsMalformedToken(string token)
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(token));
    }

    [Test]
    public void DecodeNullUsesNonSensitiveException()
    {
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));

        var exception = Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(null!));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("Send confirmation is invalid or expired."));
            Assert.That(exception.InnerException, Is.Null);
        });
    }

    [Test]
    public void DecodeRejectsSignedMalformedJson()
    {
        var bytes = Encoding.UTF8.GetBytes("not-json");
        var signature = HMACSHA256.HashData(Key, bytes);
        var token = $"{Base64UrlEncode(bytes)}.{Base64UrlEncode(signature)}";
        var codec = new HmacSendConfirmationCodec(Key, new FakeTimeProvider(Now));

        Assert.Throws<InvalidSendConfirmationException>(() => codec.Decode(token));
    }

    [Test]
    public void ConstructorRequiresAtLeast32ByteKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new HmacSendConfirmationCodec(new byte[31], new FakeTimeProvider(Now)));
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}
