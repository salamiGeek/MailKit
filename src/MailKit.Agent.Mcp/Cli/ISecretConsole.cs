using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MailKit.Agent.Mcp.Cli;

public interface ISecretConsole
{
	ValueTask<SecretBuffer> ReadSecretAsync(string prompt, CancellationToken cancellationToken);

	void WriteLine(string value);
}

public sealed class SecretBuffer : IDisposable
{
	private char[]? _characters;

	public SecretBuffer(ReadOnlySpan<char> characters)
	{
		_characters = characters.ToArray();
	}

	public ReadOnlyMemory<char> Characters
	{
		get
		{
			ObjectDisposedException.ThrowIf(_characters is null, this);
			return _characters;
		}
	}

	public void Dispose()
	{
		var characters = Interlocked.Exchange(ref _characters, null);
		if (characters is null)
			return;

		CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
	}
}
