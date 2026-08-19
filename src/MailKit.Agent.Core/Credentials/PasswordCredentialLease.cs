using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MailKit.Agent.Core.Credentials;

public sealed class PasswordCredentialLease : IDisposable
{
    private char[]? _password;

    private PasswordCredentialLease(char[] password)
    {
        _password = password;
    }

    public static PasswordCredentialLease FromCharacters(ReadOnlySpan<char> password) =>
        new(password.ToArray());

    public NetworkCredential CreateNetworkCredential(string username)
    {
        ObjectDisposedException.ThrowIf(_password is null, this);
        ArgumentNullException.ThrowIfNull(username);
        return new NetworkCredential(username, new string(_password));
    }

    public void Dispose()
    {
        var password = Interlocked.Exchange(ref _password, null);
        if (password is null)
            return;

        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
    }
}
