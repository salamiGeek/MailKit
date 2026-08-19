using System.Diagnostics;
using System.Text.Json;

namespace MailKit.Agent.Mcp.Tests.EndToEnd;

public class FoundationServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(300);

    [Test]
    public async Task FoundationToolsRunOverStdioWithIsolatedAccountStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        await using var server = await StdioMcpServer.StartAsync(
            "MailKit Agent test",
            repositoryRoot,
            [ResolveServerAssembly(repositoryRoot)]);
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var health = await server.Client.CallToolAsync(
            "diagnostics_health",
            cancellationToken: cancellation.Token);
        var accounts = await server.Client.CallToolAsync(
            "account_list",
            cancellationToken: cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(health.IsError, Is.Not.True);
            Assert.That(health.StructuredContent, Is.Not.Null);
            Assert.That(
                health.StructuredContent!.Value.GetProperty("data").GetProperty("transport").GetString(),
                Is.EqualTo("stdio"));
            Assert.That(
                health.StructuredContent.Value.GetProperty("data")
                    .GetProperty("network_listener_enabled").GetBoolean(),
                Is.False);

            Assert.That(accounts.IsError, Is.Not.True);
            Assert.That(accounts.StructuredContent, Is.Not.Null);
            var accountData = accounts.StructuredContent!.Value.GetProperty("data");
            Assert.That(accountData.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(accountData.GetArrayLength(), Is.Zero);
        });
    }

	[Test]
	public async Task FakeGatewaysServeFullProtocolWorkflowOverStdio()
	{
#if !DEBUG
		// The fake gateways are compiled into the server only in DEBUG builds and the
		// Release server rejects MAILKIT_AGENT_TEST_MODE by design (proven separately
		// by PublishedReleaseOutputRejectsTestMode), so this e2e runs only against a
		// Debug server build. Accepted trade-off: Release CI skips this flow.
		Assert.Ignore("fake gateways require a Debug server build; MAILKIT_AGENT_TEST_MODE is rejected by Release builds");
#endif
		var repositoryRoot = FindRepositoryRoot();
        await using var server = await StdioMcpServer.StartAsync(
            "MailKit Agent fixture test",
            repositoryRoot,
            [ResolveServerAssembly(repositoryRoot)],
            testFixtures: ["credential", "connection", "imap", "pop3", "smtp"]);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        const string accountId = "e2e-fixture";

        var put = await server.Client.CallToolAsync(
            "account_profile_put",
            new Dictionary<string, object?>
            {
                ["profile"] = new Dictionary<string, object?>
                {
                    ["id"] = accountId,
                    ["display_name"] = "Fixture Account",
                    ["username"] = "fixture@example.test",
                    ["authentication"] = "password",
                    ["imap"] = Endpoint("imap.example.test", 993),
                    ["pop3"] = Endpoint("pop3.example.test", 995),
                    ["smtp"] = Endpoint("smtp.example.test", 465)
                }
            },
            cancellationToken: cancellation.Token);
        Assert.That(put.StructuredContent!.Value.GetProperty("ok").GetBoolean(), Is.True);

        var credentialStatus = await server.Client.CallToolAsync(
            "account_credential_status",
            new Dictionary<string, object?>
            {
                ["request"] = new Dictionary<string, object?> { ["account_id"] = accountId }
            },
            cancellationToken: cancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(
                credentialStatus.StructuredContent!.Value.GetProperty("ok").GetBoolean(),
                Is.True);
            Assert.That(
                credentialStatus.StructuredContent.Value.GetProperty("data")
                    .GetProperty("configured").GetBoolean(),
                Is.True);
        });

        var connection = await server.Client.CallToolAsync(
            "account_connection_test",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId
            } },
            cancellationToken: cancellation.Token);
        var connectionData = connection.StructuredContent!.Value.GetProperty("data");
        Assert.That(
            connectionData.EnumerateArray()
                .Select(item => item.GetProperty("connected").GetBoolean()),
            Is.All.True);

        var folders = await server.Client.CallToolAsync(
            "folder_list",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId
            } },
            cancellationToken: cancellation.Token);
        Assert.That(
            folders.StructuredContent!.Value.GetProperty("data")[0].GetProperty("id").GetString(),
            Is.EqualTo("INBOX"));

        var messages = await server.Client.CallToolAsync(
            "message_list",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["folder_id"] = "INBOX",
                ["page_size"] = 2
            } },
            cancellationToken: cancellation.Token);
        var messagesData = messages.StructuredContent!.Value.GetProperty("data");
        Assert.That(messagesData.GetProperty("messages").GetArrayLength(), Is.EqualTo(2));

        Dictionary<string, object?> reference = messagesData.GetProperty("messages")[0]
            .GetProperty("reference")
            .Deserialize<Dictionary<string, object?>>()!;
        Assert.That(
            ((JsonElement)reference["account_id"]!).GetString(),
            Is.EqualTo(accountId));

        var read = await server.Client.CallToolAsync(
            "message_read",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["reference"] = reference
            } },
            cancellationToken: cancellation.Token);
        var readData = read.StructuredContent!.Value.GetProperty("data");
        Assert.Multiple(() =>
        {
            Assert.That(readData.GetProperty("untrusted").GetBoolean(), Is.True);
            Assert.That(readData.GetProperty("attachments").GetArrayLength(), Is.EqualTo(1));
        });

        var savedAttachment = await server.Client.CallToolAsync(
            "attachment_save",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["reference"] = reference,
                ["attachment_id"] = "att-1",
                ["destination_name"] = "e2e-fixture.txt"
            } },
            cancellationToken: cancellation.Token);
        var saveData = savedAttachment.StructuredContent!.Value.GetProperty("data");
        string savedPath = saveData.GetProperty("path").GetString()!;
        Assert.Multiple(() =>
        {
            Assert.That(
                Path.GetFullPath(savedPath),
                Does.StartWith(Path.GetFullPath(server.DataDirectory)));
            Assert.That(File.ReadAllText(savedPath), Is.EqualTo("fixture attachment payload"));
        });

        var pop3Messages = await server.Client.CallToolAsync(
            "pop3_message_list",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["page_size"] = 5
            } },
            cancellationToken: cancellation.Token);
        Dictionary<string, object?> pop3Reference = pop3Messages.StructuredContent!
            .Value.GetProperty("data").GetProperty("messages")[0]
            .GetProperty("reference")
            .Deserialize<Dictionary<string, object?>>()!;

        var pop3Read = await server.Client.CallToolAsync(
            "pop3_message_read",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["reference"] = pop3Reference
            } },
            cancellationToken: cancellation.Token);
        var pop3ReadData = pop3Read.StructuredContent!.Value.GetProperty("data");
        Assert.Multiple(() =>
        {
            Assert.That(pop3ReadData.GetProperty("untrusted").GetBoolean(), Is.True);
            Assert.That(pop3ReadData.GetProperty("read_state_supported").GetBoolean(), Is.False);
            Assert.That(
                pop3ReadData.GetProperty("is_read").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });

        var prepared = await server.Client.CallToolAsync(
            "send_prepare",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["draft"] = new Dictionary<string, object?>
                {
                    ["to"] = new object[] { new Dictionary<string, object?>
                    {
                        ["address"] = "recipient@example.test"
                    } },
                    ["subject"] = "Fixture e2e send",
                    ["text_body"] = "Fixture e2e body."
                },
                ["idempotency_key"] = "e2e-key-1"
            } },
            cancellationToken: cancellation.Token);
        var preview = prepared.StructuredContent!.Value.GetProperty("data");
        string confirmationToken = preview.GetProperty("confirmation_token").GetString()!;
        Assert.That(confirmationToken, Is.Not.Null.And.Not.Empty);

        var committed = await server.Client.CallToolAsync(
            "send_commit",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["confirmation_token"] = confirmationToken
            } },
            cancellationToken: cancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(
                committed.StructuredContent!.Value.GetProperty("ok").GetBoolean(),
                Is.True);
            Assert.That(
                committed.StructuredContent.Value.GetProperty("data")
                    .GetProperty("state").GetString(),
                Is.EqualTo("succeeded"));
        });

        var repeatedCommit = await server.Client.CallToolAsync(
            "send_commit",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["confirmation_token"] = confirmationToken
            } },
            cancellationToken: cancellation.Token);
        Assert.That(
            repeatedCommit.StructuredContent!.Value.GetProperty("ok").GetBoolean(),
            Is.False);

        var status = await server.Client.CallToolAsync(
            "send_status",
            new Dictionary<string, object?> { ["request"] = new Dictionary<string, object?>
            {
                ["account_id"] = accountId,
                ["idempotency_key"] = "e2e-key-1"
            } },
            cancellationToken: cancellation.Token);
        Assert.That(
            status.StructuredContent!.Value.GetProperty("data")
                .GetProperty("state").GetString(),
            Is.EqualTo("succeeded"));

        string deliveryLogPath = Path.Combine(
            server.DataDirectory, "test-fixtures", "smtp-deliveries.jsonl");
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(deliveryLogPath), Is.True);
            Assert.That(
                File.ReadAllLines(deliveryLogPath).Length,
                Is.EqualTo(1),
                "A repeated commit must never deliver a second message.");
            var standardError = string.Join(Environment.NewLine, server.StandardError);
            Assert.That(standardError, Does.Not.Contain("fixture-password"));
            Assert.That(standardError, Does.Not.Contain("Exception"));
        });
    }

    [Test]
    public async Task PublishedReleaseOutputRejectsTestMode()
    {
        var repositoryRoot = FindRepositoryRoot();
        var publishRoot = Path.Combine(
            Path.GetTempPath(),
            "mailkit-agent-release-tests-" + Guid.NewGuid().ToString("N"));
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "mailkit-agent-release-data-" + Guid.NewGuid().ToString("N"));
        try
        {
            var publishResult = await RunDotnetAsync(
                [
                    "publish",
                    Path.Combine(repositoryRoot, "src", "MailKit.Agent.Mcp", "MailKit.Agent.Mcp.csproj"),
                    "-c", "Release",
                    "-f", "net8.0",
                    "-o", publishRoot,
                    "-p:TargetFramework=net8.0",
                    "-p:TargetFrameworks=net8.0"
                ],
                TimeSpan.FromMinutes(8),
                testMode: false,
                dataDirectory);
            Assert.That(
                publishResult.ExitCode,
                Is.Zero,
                "Release publish failed: " + publishResult.Output);

            var runResult = await RunDotnetAsync(
                [Path.Combine(publishRoot, "mailkit-agent.dll")],
                TimeSpan.FromSeconds(30),
                testMode: true,
                dataDirectory);

            Assert.Multiple(() =>
            {
                Assert.That(runResult.ExitCode, Is.Not.Zero);
                Assert.That(
                    runResult.Output,
                    Does.Contain("MAILKIT_AGENT_TEST_MODE").IgnoreCase);
            });
        }
        finally
        {
            if (Directory.Exists(publishRoot))
                Directory.Delete(publishRoot, recursive: true);
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static Dictionary<string, object?> Endpoint(string host, int port) =>
        new()
        {
            ["host"] = host,
            ["port"] = port,
            ["tls"] = "implicit_tls"
        };

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        bool testMode,
        string dataDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["MAILKIT_AGENT_DATA_DIR"] = dataDirectory;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        if (testMode)
            startInfo.Environment["MAILKIT_AGENT_TEST_MODE"] = "1";

        using var cancellation = new CancellationTokenSource(timeout);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var errorTask = process.StandardError.ReadToEndAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static string ResolveServerAssembly(string repositoryRoot)
    {
        var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory)
            .Parent?.Name ?? "Debug";
        return Path.Combine(
            repositoryRoot,
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
