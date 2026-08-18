namespace MailKit.Agent.Core.Paging;

public interface ICursorCodec
{
    string Encode(CursorPayload payload);

    CursorPayload Decode(string token);
}
