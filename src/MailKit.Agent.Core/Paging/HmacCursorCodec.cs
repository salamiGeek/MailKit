using System.Security.Cryptography;
using System.Text.Json;

namespace MailKit.Agent.Core.Paging;

public sealed class HmacCursorCodec : ICursorCodec
{
    private readonly byte[] key;
    private readonly TimeProvider timeProvider;

    public HmacCursorCodec(byte[] key, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length < 32)
            throw new ArgumentException("Cursor key must be at least 32 bytes.", nameof(key));

        this.key = key.ToArray();
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string Encode(CursorPayload payload)
    {
        if (payload.ExpiresAt <= timeProvider.GetUtcNow())
            throw new ArgumentException("Cursor expiry must be in the future.", nameof(payload));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(key, bytes);
        return $"{Base64UrlEncode(bytes)}.{Base64UrlEncode(signature)}";
    }

    public CursorPayload Decode(string token)
    {
        try
        {
            if (token is null)
                throw new InvalidCursorException();

            var parts = token.Split('.');
            if (parts.Length != 2)
                throw new InvalidCursorException();

            var bytes = Base64UrlDecode(parts[0]);
            var supplied = Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(key, bytes);

            if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                throw new InvalidCursorException();

            var payload = JsonSerializer.Deserialize<CursorPayload>(bytes)
                ?? throw new InvalidCursorException();

            if (payload.ExpiresAt <= timeProvider.GetUtcNow())
                throw new InvalidCursorException();

            return payload;
        }
        catch (InvalidCursorException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException)
        {
            throw new InvalidCursorException();
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException()
        };

        var bytes = Convert.FromBase64String(base64);
        if (!string.Equals(Base64UrlEncode(bytes), value, StringComparison.Ordinal))
            throw new FormatException();

        return bytes;
    }
}
