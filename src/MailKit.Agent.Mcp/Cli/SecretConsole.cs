using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MailKit.Agent.Mcp.Cli;

public sealed class SecretConsole : ISecretConsole
{
	private static readonly TimeSpan KeyPollInterval = TimeSpan.FromMilliseconds(25);
	private readonly ISecretConsoleInput _input;

	public SecretConsole()
		: this(new SystemSecretConsoleInput())
	{
	}

	internal SecretConsole(ISecretConsoleInput input)
	{
		_input = input ?? throw new ArgumentNullException(nameof(input));
	}

	public async ValueTask<SecretBuffer> ReadSecretAsync(
		string prompt,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(prompt);
		cancellationToken.ThrowIfCancellationRequested();
		_input.Write(prompt);

		var characters = new char[128];
		var length = 0;
		var previousTreatControlCAsInput = _input.TreatControlCAsInput;
		try
		{
			_input.TreatControlCAsInput = true;
			while (true)
			{
				while (!_input.KeyAvailable)
					await Task.Delay(KeyPollInterval, cancellationToken);

				cancellationToken.ThrowIfCancellationRequested();
				var key = _input.ReadKey(intercept: true);
				if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
					throw new OperationCanceledException(cancellationToken);
				if (key.Key == ConsoleKey.Enter)
					return new SecretBuffer(characters.AsSpan(0, length));
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
			_input.TreatControlCAsInput = previousTreatControlCAsInput;
			_input.WriteLine();
		}
	}

	public void WriteLine(string value) => Console.WriteLine(value);
}

internal interface ISecretConsoleInput
{
	bool KeyAvailable { get; }

	bool TreatControlCAsInput { get; set; }

	ConsoleKeyInfo ReadKey(bool intercept);

	void Write(string value);

	void WriteLine();
}

internal sealed class SystemSecretConsoleInput : ISecretConsoleInput
{
	public bool KeyAvailable => Console.KeyAvailable;

	public bool TreatControlCAsInput
	{
		get => Console.TreatControlCAsInput;
		set => Console.TreatControlCAsInput = value;
	}

	public ConsoleKeyInfo ReadKey(bool intercept) => Console.ReadKey(intercept);

	public void Write(string value) => Console.Write(value);

	public void WriteLine() => Console.WriteLine();
}
