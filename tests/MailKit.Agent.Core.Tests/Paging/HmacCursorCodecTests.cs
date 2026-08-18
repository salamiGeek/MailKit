using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Agent.Core.Paging;

namespace MailKit.Agent.Core.Tests.Paging;

public class HmacCursorCodecTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void CursorRoundTrips()
    {
        var time = new FakeTimeProvider(Now);
        var codec = new HmacCursorCodec(Key, time);
        var payload = new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10));

        var decoded = codec.Decode(codec.Encode(payload));

        Assert.That(decoded, Is.EqualTo(payload));
    }

    [Test]
    public void CursorPayloadUsesStableSnakeCaseJsonNames()
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));
        var payloadSegment = token[..token.IndexOf('.')];
        using var document = JsonDocument.Parse(Base64UrlDecode(payloadSegment));

        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name);

        Assert.That(propertyNames, Is.EquivalentTo(new[]
        {
            "account_id",
            "scope",
            "position",
            "expires_at"
        }));
    }

    [Test]
    public void CursorRejectsTampering()
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));
        var separator = token.IndexOf('.');
        var replacement = token[separator + 1] == 'A' ? 'B' : 'A';
        var tampered = token[..(separator + 1)] + replacement + token[(separator + 2)..];

        Assert.Throws<InvalidCursorException>(() => codec.Decode(tampered));
    }

    [TestCase('-', '+')]
    [TestCase('_', '/')]
    public void DecodeRejectsNonUrlSafeCharacters(char urlSafeCharacter, char standardBase64Character)
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));
        var separator = token.IndexOf('.');
        var signature = token[(separator + 1)..];
        Assert.That(signature, Does.Contain(urlSafeCharacter.ToString()), "Test token must exercise the replacement.");
        var nonUrlSafe = token[..(separator + 1)] + signature.Replace(urlSafeCharacter, standardBase64Character);

        Assert.Throws<InvalidCursorException>(() => codec.Decode(nonUrlSafe));
    }

    [Test]
    public void DecodeRejectsBase64UrlPadding()
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));

        Assert.Throws<InvalidCursorException>(() => codec.Decode(token + "="));
    }

    [Test]
    public void DecodeRejectsNonCanonicalTailBits()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));
        var lastIndex = alphabet.IndexOf(token[^1]);
        Assert.That(lastIndex % 4, Is.Zero, "A 32-byte signature must end with four data bits and two zero bits.");
        var nonCanonical = token[..^1] + alphabet[lastIndex + 1];

        Assert.Throws<InvalidCursorException>(() => codec.Decode(nonCanonical));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void EncodeRequiresFutureExpiry(int minuteOffset)
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var payload = new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(minuteOffset));

        Assert.Throws<ArgumentException>(() => codec.Encode(payload));
    }

    [Test]
    public void DecodeRejectsCursorAtExpiryBoundary()
    {
        var time = new FakeTimeProvider(Now);
        var codec = new HmacCursorCodec(Key, time);
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));
        time.SetUtcNow(Now.AddMinutes(10));

        Assert.Throws<InvalidCursorException>(() => codec.Decode(token));
    }

    [TestCase("")]
    [TestCase("payload")]
    [TestCase("payload.signature.extra")]
    [TestCase("a.invalid!")]
    public void DecodeRejectsMalformedToken(string token)
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));

        Assert.Throws<InvalidCursorException>(() => codec.Decode(token));
    }

    [Test]
    public void DecodeNullUsesNonSensitiveInvalidCursorException()
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));

        var exception = Assert.Throws<InvalidCursorException>(() => codec.Decode(null!));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("Cursor is invalid or expired."));
            Assert.That(exception.InnerException, Is.Null);
        });
    }

    [Test]
    public void DecodeRejectsSignedMalformedJson()
    {
        var bytes = Encoding.UTF8.GetBytes("not-json");
        var signature = HMACSHA256.HashData(Key, bytes);
        var token = $"{Base64UrlEncode(bytes)}.{Base64UrlEncode(signature)}";
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));

        Assert.Throws<InvalidCursorException>(() => codec.Decode(token));
    }

    [Test]
    public void InvalidCursorExceptionDoesNotExposeTokenOrPayload()
    {
        const string secretAccount = "secret-account";
        const string secretPosition = "secret-position";
        var time = new FakeTimeProvider(Now);
        var codec = new HmacCursorCodec(Key, time);
        var token = codec.Encode(new CursorPayload(secretAccount, "INBOX", secretPosition, Now.AddMinutes(1)));
        time.SetUtcNow(Now.AddMinutes(1));

        var exception = Assert.Throws<InvalidCursorException>(() => codec.Decode(token));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Is.EqualTo("Cursor is invalid or expired."));
            Assert.That(exception.Message, Does.Not.Contain(token));
            Assert.That(exception.Message, Does.Not.Contain(secretAccount));
            Assert.That(exception.Message, Does.Not.Contain(secretPosition));
            Assert.That(exception.InnerException, Is.Null);
        });
    }

    [Test]
    public void ConstructorRequiresAtLeast32ByteKey()
    {
        Assert.Throws<ArgumentException>(() =>
            new HmacCursorCodec(new byte[31], new FakeTimeProvider(Now)));
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
