using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MailKit.Agent.Mcp.Cli;

public sealed class SecretConsole : ISecretConsole
{
	public ValueTask<SecretBuffer> ReadSecretAsync(
		string prompt,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(prompt);
		cancellationToken.ThrowIfCancellationRequested();
		Console.Write(prompt);

		var characters = new char[128];
		var length = 0;
		var previousTreatControlCAsInput = Console.TreatControlCAsInput;
		try
		{
			Console.TreatControlCAsInput = true;
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var key = Console.ReadKey(intercept: true);
				if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
					throw new OperationCanceledException(cancellationToken);
				if (key.Key == ConsoleKey.Enter)
					return ValueTask.FromResult(new SecretBuffer(characters.AsSpan(0, length)));
				if (key.Key == ConsoleKey.Backspace)
				{
					if (length > 0)
						characters[--length] = '\0';
					continue;
				}
				if (key.KeyChar == '\0')
					continue;

				if (length == characters.Length)
				{
					var expanded = new char[checked(characters.Length * 2)];
					characters.CopyTo(expanded, 0);
					CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
					characters = expanded;
				}
				characters[length++] = key.KeyChar;
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
			Console.TreatControlCAsInput = previousTreatControlCAsInput;
			Console.WriteLine();
		}
	}

	public void WriteLine(string value) => Console.WriteLine(value);
}
