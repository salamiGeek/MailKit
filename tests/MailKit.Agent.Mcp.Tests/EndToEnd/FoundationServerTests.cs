using System.Text.Json;

namespace MailKit.Agent.Mcp.Tests.EndToEnd;

public class FoundationServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Test]
    public async Task FoundationToolsRunOverStdioWithIsolatedAccountStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        await using var server = await StdioMcpServer.StartAsync(
            "MailKit Agent test",
            repositoryRoot,
            ResolveServerAssembly(repositoryRoot));
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
