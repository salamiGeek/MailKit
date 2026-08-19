namespace MailKit.Agent.Core.Sending;

/// <summary>
/// Thrown when a send confirmation token cannot be verified, has expired, or is
/// malformed. The message is constant and never includes the token or its payload.
/// </summary>
public sealed class InvalidSendConfirmationException : Exception
{
    public InvalidSendConfirmationException()
        : base("Send confirmation is invalid or expired.")
    {
    }
}

public interface ISendConfirmationCodec
{
    string Encode(SendConfirmationPayload payload);

    SendConfirmationPayload Decode(string token);
}
