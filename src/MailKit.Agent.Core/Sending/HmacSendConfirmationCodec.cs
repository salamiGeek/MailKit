using System.Security.Cryptography;
using System.Text.Json;

namespace MailKit.Agent.Core.Sending;

/// <summary>
/// HMAC-SHA256 confirmation codec following the canonical <c>HmacCursorCodec</c>
/// pattern: base64url payload and signature, fixed-time signature comparison,
/// canonical base64url rejection, and expiry enforced at encode and decode time.
/// </summary>
public sealed class HmacSendConfirmationCodec : ISendConfirmationCodec
{
    private readonly byte[] key;
    private readonly TimeProvider timeProvider;

    public HmacSendConfirmationCodec(byte[] key, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length < 32)
            throw new ArgumentException("Confirmation key must be at least 32 bytes.", nameof(key));

        this.key = key.ToArray();
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string Encode(SendConfirmationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.ExpiresAt <= timeProvider.GetUtcNow())
            throw new ArgumentException("Confirmation expiry must be in the future.", nameof(payload));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(key, bytes);
        return $"{Base64UrlEncode(bytes)}.{Base64UrlEncode(signature)}";
    }

    public SendConfirmationPayload Decode(string token)
    {
        try
        {
            if (token is null)
                throw new InvalidSendConfirmationException();

            var parts = token.Split('.');
            if (parts.Length != 2)
                throw new InvalidSendConfirmationException();

            var bytes = Base64UrlDecode(parts[0]);
            var supplied = Base64UrlDecode(parts[1]);
            var expected = HMACSHA256.HashData(key, bytes);

            if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
                throw new InvalidSendConfirmationException();

            var payload = JsonSerializer.Deserialize<SendConfirmationPayload>(bytes)
                ?? throw new InvalidSendConfirmationException();

            if (payload.ExpiresAt <= timeProvider.GetUtcNow())
                throw new InvalidSendConfirmationException();

            return payload;
        }
        catch (InvalidSendConfirmationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or CryptographicException)
        {
            throw new InvalidSendConfirmationException();
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
