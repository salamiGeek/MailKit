using System.Diagnostics;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

namespace MailKit.Agent.Mcp.Tests.Live;

/// <summary>
/// Always-on guards for the opt-in live smoke tooling. These tests run in every
/// normal test run and CI: they prove the live fixture can never execute without an
/// explicit selection, that the wrapper script accepts only non-secret parameters,
/// and that a fresh fixture data directory never persists password or token values.
/// The live fixture itself never runs here.
/// </summary>
public sealed class LiveSmokeGuardTests
{
	private static readonly string[] WrapperParameterLines =
	{
		"[Parameter(Mandatory)] [string] $AccountId",
		"[Parameter(Mandatory)] [string] $Username",
		"[Parameter(Mandatory)] [string] $ImapHost",
		"[int] $ImapPort = 993",
		"[Parameter(Mandatory)] [string] $Pop3Host",
		"[int] $Pop3Port = 995",
		"[Parameter(Mandatory)] [string] $SmtpHost",
		"[int] $SmtpPort = 465",
		"[string] $Recipient",
		"[switch] $ConfirmMarkRead",
		"[switch] $ConfirmSend"
	};

	private static string RepositoryRoot { get; } = FindRepositoryRoot();

	private static string WrapperScriptPath =>
		Path.Combine(RepositoryRoot, "scripts", "Test-MailKitAgentLive.ps1");

	private static string WrapperScriptText => File.ReadAllText(WrapperScriptPath);

	[Test]
	public void LiveProtocolFixtureIsExplicitAndDocumentsItsOptInReason()
	{
		var attribute = typeof(LiveProtocolTests)
			.GetCustomAttributes(typeof(ExplicitAttribute), inherit: false)
			.OfType<ExplicitAttribute>()
			.SingleOrDefault();

		Assert.That(attribute, Is.Not.Null, "LiveProtocolTests must be marked [Explicit].");

		var suite = new TestSuite("live-suite");
		attribute!.ApplyToTest(suite);
		Assert.Multiple(() =>
		{
			Assert.That(suite.RunState, Is.EqualTo(RunState.Explicit));
			Assert.That(
				(string?)suite.Properties.Get(PropertyNames.SkipReason),
				Does.Contain("user-configured real mail server").And.Contain("Windows credential"));
		});
	}

	[Test]
	public void LiveWrapperScriptDeclaresExactlyTheNonSecretParameterBlock()
	{
		var script = WrapperScriptText;

		Assert.Multiple(() =>
		{
			foreach (string parameterLine in WrapperParameterLines)
				Assert.That(script, Does.Contain(parameterLine), $"Missing parameter: {parameterLine}");

			// The wrapper must never accept a secret-valued parameter.
			Assert.That(script, Does.Not.Match("(?i)\\$\\w*(password|passwd|secret|token|credential_value)\\w*"));
			// SMTP defaults to port 465 with implicit TLS.
			Assert.That(script, Does.Contain("465"));
			Assert.That(script, Does.Contain(LiveProtocolTests.DefaultTls));
		});
	}

	[Test]
	public void LiveWrapperScriptRunsOnlyTheExplicitLiveFilterInAnIsolatedDataDirectory()
	{
		var script = WrapperScriptText;

		Assert.Multiple(() =>
		{
			Assert.That(
				script,
				Does.Contain("--filter"),
				"The wrapper must invoke only an explicit test filter.");
			Assert.That(
				script,
				Does.Contain("FullyQualifiedName~MailKit.Agent.Mcp.Tests.Live.LiveProtocolTests"),
				"The wrapper filter must select exactly the explicit live fixture.");
			Assert.That(
				script,
				Does.Not.Contain("MAILKIT_AGENT_TEST_MODE"),
				"The wrapper must never activate fake gateways.");
			Assert.That(
				script,
				Does.Contain("[IO.Path]::GetTempPath()"),
				"The wrapper must build its data directory under the system temp directory.");
			Assert.That(script, Does.Contain("mailkit-agent-live-"));
		});
	}

	[Test]
	public async Task LiveWrapperScriptRefusesConfirmSendWithoutRecipient()
	{
		var result = await RunWrapperScriptAsync(
			"-AccountId", "live-guard",
			"-Username", "user@example.test",
			"-ImapHost", "imap.example.test",
			"-Pop3Host", "pop3.example.test",
			"-SmtpHost", "smtp.example.test",
			"-ConfirmSend");

		Assert.Multiple(() =>
		{
			Assert.That(result.ExitCode, Is.Not.Zero, "The wrapper must fail when -ConfirmSend has no -Recipient.");
			Assert.That(result.Output, Does.Contain("Recipient").IgnoreCase);
			Assert.That(result.Output, Does.Not.Contain("Passed").IgnoreCase, "No test run may start.");
		});
	}

	[Test]
	public void LiveWrapperScriptPreviewsTheExactDraftBeforeRequiringInteractiveSend()
	{
		var script = WrapperScriptText;

		Assert.Multiple(() =>
		{
			Assert.That(script, Does.Contain(LiveProtocolTests.SendSubject));
			Assert.That(script, Does.Contain(LiveProtocolTests.SendTextBody));
			Assert.That(script, Does.Contain("Read-Host"), "The wrapper must require an interactive confirmation.");
			Assert.That(script, Does.Contain("SEND"), "The second confirmation must be typing SEND.");
		});
	}

	[Test]
	public async Task FreshDataDirectoryContainsNoSecretPropertyNamesInAnyJsonFile()
	{
		// Real server, no test-mode fakes: valid for Debug and Release builds. The
		// account profile store and the send ledger are the only JSON writers in a
		// fresh data directory, so drive one profile put and one send_prepare (which
		// composes MIME and writes the ledger without touching credentials or SMTP).
		await using var server = await StdioMcpServer.StartAsync(
			"MailKit Agent data scan test",
			RepositoryRoot,
			[ResolveServerAssembly()]);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		const string accountId = "live-guard-data-scan";

		var put = await server.Client.CallToolAsync(
			"account_profile_put",
			new Dictionary<string, object?>
			{
				["profile"] = Profile(accountId)
			},
			cancellationToken: cancellation.Token);
		Assert.That(put.StructuredContent!.Value.GetProperty("ok").GetBoolean(), Is.True);

		var prepared = await server.Client.CallToolAsync(
			"send_prepare",
			SendPrepareRequest(accountId),
			cancellationToken: cancellation.Token);
		string token = prepared.StructuredContent!.Value.GetProperty("data")
			.GetProperty("confirmation_token").GetString()!;
		Assert.That(token, Is.Not.Null.And.Not.Empty);

		string[] jsonFiles = CollectJsonFiles(server.DataDirectory);
		Assert.Multiple(() =>
		{
			Assert.That(
				jsonFiles,
				Does.Contain(Path.Combine(server.DataDirectory, "accounts", accountId + ".json")),
				"The scan must cover the persisted account profile.");
			Assert.That(
				jsonFiles,
				Has.Some.Match("send-ledger"),
				"The scan must cover the persisted send ledger.");
		});
		ScanForSecretShapes(jsonFiles, secretValue: null);
	}

#if DEBUG
	[Test]
	public async Task FakeVaultPasswordNeverPersistsIntoDataDirectoryJson()
	{
		// Debug-only, mirroring the server's own #if DEBUG fake-gateway gating: with
		// the fake credential vault the password value exists in-process, so a full
		// prepare/commit must be proven not to leak it into any persisted JSON.
		await using var server = await StdioMcpServer.StartAsync(
			"MailKit Agent fake vault scan test",
			RepositoryRoot,
			[ResolveServerAssembly()],
			testFixtures: ["credential", "smtp"]);
		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		const string accountId = "live-guard-fake-vault-scan";

		var put = await server.Client.CallToolAsync(
			"account_profile_put",
			new Dictionary<string, object?> { ["profile"] = Profile(accountId) },
			cancellationToken: cancellation.Token);
		Assert.That(put.StructuredContent!.Value.GetProperty("ok").GetBoolean(), Is.True);

		var prepared = await server.Client.CallToolAsync(
			"send_prepare",
			SendPrepareRequest(accountId),
			cancellationToken: cancellation.Token);
		string token = prepared.StructuredContent!.Value.GetProperty("data")
			.GetProperty("confirmation_token").GetString()!;
		Assert.That(token, Is.Not.Null.And.Not.Empty);

		var committed = await server.Client.CallToolAsync(
			"send_commit",
			new Dictionary<string, object?>
			{
				["request"] = new Dictionary<string, object?> { ["confirmation_token"] = token }
			},
			cancellationToken: cancellation.Token);
		Assert.That(committed.StructuredContent!.Value.GetProperty("ok").GetBoolean(), Is.True);

		ScanForSecretShapes(CollectJsonFiles(server.DataDirectory), secretValue: "fixture-password");
	}
#endif

	private static Dictionary<string, object?> Profile(string accountId) =>
		new()
		{
			["id"] = accountId,
			["display_name"] = "Data scan account",
			["username"] = "user@example.test",
			["authentication"] = "password",
			["imap"] = Endpoint("imap.example.test", 993),
			["pop3"] = Endpoint("pop3.example.test", 995),
			["smtp"] = Endpoint("smtp.example.test", 465)
		};

	private static Dictionary<string, object?> Endpoint(string host, int port) =>
		new()
		{
			["host"] = host,
			["port"] = port,
			["tls"] = "implicit_tls"
		};

	private static Dictionary<string, object?> SendPrepareRequest(string accountId) =>
		new()
		{
			["request"] = new Dictionary<string, object?>
			{
				["account_id"] = accountId,
				["draft"] = new Dictionary<string, object?>
				{
					["to"] = new object[] { new Dictionary<string, object?> { ["address"] = "recipient@example.test" } },
					["subject"] = LiveProtocolTests.SendSubject,
					["text_body"] = LiveProtocolTests.SendTextBody
				},
				["idempotency_key"] = "live-guard-scan-key"
			}
		};

	private static string[] CollectJsonFiles(string dataDirectory) =>
		Directory.EnumerateFiles(dataDirectory, "*.json", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(dataDirectory, "*.jsonl", SearchOption.AllDirectories))
			.ToArray();

	private static void ScanForSecretShapes(string[] jsonFiles, string? secretValue)
	{
		Assert.That(jsonFiles, Is.Not.Empty, "The flow must have persisted JSON state to scan.");

		string[] secretNameFragments = ["password", "passwd", "token", "secret", "credential_value", "authorization"];
		foreach (string path in jsonFiles)
		{
			string content = File.ReadAllText(path);
			if (secretValue is not null)
			{
				Assert.That(
					content,
					Does.Not.Contain(secretValue),
					$"The in-process password value leaked into {path}.");
			}

			foreach (string property in ExtractJsonPropertyNames(content))
			{
				Assert.That(
					secretNameFragments.Any(fragment =>
						property.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
					Is.False,
					$"{path} persists a secret-shaped property name '{property}'.");
			}
		}
	}

	private static IEnumerable<string> ExtractJsonPropertyNames(string json)
	{
		foreach (System.Text.RegularExpressions.Match match in
		         System.Text.RegularExpressions.Regex.Matches(json, "\"([^\"]+)\"\\s*:"))
		{
			yield return match.Groups[1].Value;
		}
	}

	private static async Task<(int ExitCode, string Output)> RunWrapperScriptAsync(
		params string[] arguments)
	{
		Assert.That(File.Exists(WrapperScriptPath), Is.True, "scripts/Test-MailKitAgentLive.ps1 is missing.");

		var startInfo = new ProcessStartInfo
		{
			FileName = OperatingSystem.IsWindows()
				? Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.System),
					"WindowsPowerShell",
					"v1.0",
					"powershell.exe")
				: "pwsh",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add("-NoProfile");
		startInfo.ArgumentList.Add("-NonInteractive");
		startInfo.ArgumentList.Add("-File");
		startInfo.ArgumentList.Add(WrapperScriptPath);
		foreach (string argument in arguments)
			startInfo.ArgumentList.Add(argument);

		using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Could not start the wrapper script process.");
		process.StandardInput.Close();
		var outputTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
		var errorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
		await process.WaitForExitAsync(cancellation.Token);
		return (process.ExitCode, await outputTask + await errorTask);
	}

	private static string ResolveServerAssembly()
	{
		var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory)
			.Parent?.Name ?? "Debug";
		return Path.Combine(
			RepositoryRoot,
			"src",
			"MailKit.Agent.Mcp",
			"bin",
			configuration,
			"net8.0",
			"mailkit-agent.dll");
	}

	private static string FindRepositoryRoot()
	{
		for (var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
		     directory is not null;
		     directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "MailKit.Agent.sln")))
				return directory.FullName;
		}

		throw new DirectoryNotFoundException("Could not find the repository root.");
	}
}
