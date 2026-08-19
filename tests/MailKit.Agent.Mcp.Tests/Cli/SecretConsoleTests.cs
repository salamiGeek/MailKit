using System.Text;
using MailKit.Agent.Mcp.Cli;

namespace MailKit.Agent.Mcp.Tests.Cli;

public class SecretConsoleTests
{
	[Test]
	public void CancellationWhileNoKeyIsAvailableInterruptsRead()
	{
		var input = new FakeSecretConsoleInput();
		var console = new SecretConsole(input);
		using var cancellation = new CancellationTokenSource();

		var readTask = console.ReadSecretAsync(
			"Credential: ",
			cancellation.Token).AsTask();
		Assert.That(readTask.IsCompleted, Is.False);

		cancellation.Cancel();
		var completion = readTask.WaitAsync(TimeSpan.FromSeconds(1));

		Assert.Multiple(() =>
		{
			Assert.CatchAsync<OperationCanceledException>(async () => await completion);
			Assert.That(input.ReadKeyCount, Is.Zero);
			Assert.That(input.Output, Is.EqualTo("Credential: " + Environment.NewLine));
			Assert.That(input.TreatControlCAsInput, Is.False);
		});
	}

	[Test]
	public async Task BackspaceEditsInputWithoutEchoingCharacters()
	{
		var input = new FakeSecretConsoleInput(
			Key('a', ConsoleKey.A),
			Key('b', ConsoleKey.B),
			Key('\b', ConsoleKey.Backspace),
			Key('c', ConsoleKey.C),
			Key('\r', ConsoleKey.Enter));
		var console = new SecretConsole(input);

		using var secret = await console.ReadSecretAsync(
			"Credential: ",
			CancellationToken.None);

		Assert.Multiple(() =>
		{
			Assert.That(secret.Characters.ToArray(), Is.EqualTo(new[] { 'a', 'c' }));
			Assert.That(input.Output, Is.EqualTo("Credential: " + Environment.NewLine));
			Assert.That(input.TreatControlCAsInput, Is.False);
		});
	}

	private static ConsoleKeyInfo Key(char character, ConsoleKey key) =>
		new(character, key, shift: false, alt: false, control: false);

	private sealed class FakeSecretConsoleInput(params ConsoleKeyInfo[] keys)
		: ISecretConsoleInput
	{
		private readonly Queue<ConsoleKeyInfo> _keys = new(keys);
		private readonly StringBuilder _output = new();

		public bool KeyAvailable => _keys.Count > 0;

		public bool TreatControlCAsInput { get; set; }

		public int ReadKeyCount { get; private set; }

		public string Output => _output.ToString();

		public ConsoleKeyInfo ReadKey(bool intercept)
		{
			if (!intercept)
				throw new InvalidOperationException("Secret input must be intercepted.");
			ReadKeyCount++;
			return _keys.Dequeue();
		}

		public void Write(string value) => _output.Append(value);

		public void WriteLine() => _output.AppendLine();
	}
}
