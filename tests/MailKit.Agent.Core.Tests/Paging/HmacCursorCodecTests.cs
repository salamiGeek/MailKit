using System.Security.Cryptography;
using System.Text;
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
    public void CursorRejectsTampering()
    {
        var codec = new HmacCursorCodec(Key, new FakeTimeProvider(Now));
        var token = codec.Encode(new CursorPayload("acct", "INBOX", "25", Now.AddMinutes(10)));
        var separator = token.IndexOf('.');
        var replacement = token[separator + 1] == 'A' ? 'B' : 'A';
        var tampered = token[..(separator + 1)] + replacement + token[(separator + 2)..];

        Assert.Throws<InvalidCursorException>(() => codec.Decode(tampered));
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

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}
