using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace MailKit.Agent.Mcp.Tests.Tools;

public class ToolSchemaTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentQueue<string> _standardError = new();
    private string? _dataDirectory;
    private McpClient? _client;

    [SetUp]
    public async Task SetUp()
    {
        _standardError.Clear();
        _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "mailkit-agent-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);

        using var cancellation = new CancellationTokenSource(TestTimeout);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "MailKit Agent schema test",
            Command = DotnetHostResolver.Resolve(),
            Arguments = [ResolveServerAssembly()],
            WorkingDirectory = FindRepositoryRoot(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = CreateServerEnvironment(_dataDirectory),
            StandardErrorLines = _standardError.Enqueue
        });

        try
        {
            _client = await McpClient.CreateAsync(transport, cancellationToken: cancellation.Token);
        }
        catch
        {
            Directory.Delete(_dataDirectory, recursive: true);
            throw;
        }
    }

    [TearDown]
    public async Task TearDown()
    {
        try
        {
            if (_client is not null)
                await _client.DisposeAsync();
        }
        finally
        {
            if (_dataDirectory is not null && Directory.Exists(_dataDirectory))
                Directory.Delete(_dataDirectory, recursive: true);
        }

        Assert.That(
            string.Join(Environment.NewLine, _standardError),
            Does.Not.Contain("MCP frame").IgnoreCase);
    }

    [Test]
    public async Task FoundationToolsAdvertiseSafeStructuredSchemas()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tools = await _client!.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.That(
            tools.Select(tool => tool.Name),
            Is.EquivalentTo(new[]
            {
                "diagnostics_health",
                "account_list",
                "account_profile_put"
            }));
        Assert.That(tools, Has.All.Matches<McpClientTool>(tool => tool.ReturnJsonSchema is not null));

        var putTool = tools.Single(tool => tool.Name == "account_profile_put");
        Assert.That(ContainsSecretName(putTool.JsonSchema), Is.False);

        var profileSchema = putTool.JsonSchema
            .GetProperty("properties")
            .GetProperty("profile");
        Assert.That(
            profileSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()),
            Does.Contain("id"));
        Assert.That(
            profileSchema.GetProperty("properties").GetProperty("authentication")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            Is.EquivalentTo(new[] { "password", "o_auth2" }));
        Assert.That(
            profileSchema.GetProperty("properties").GetProperty("imap")
                .GetProperty("properties").GetProperty("tls")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            Is.EquivalentTo(new[] { "plain", "start_tls", "implicit_tls" }));
    }

    [Test]
    public async Task FoundationToolsReturnStructuredContentAndInvalidPutIsSanitized()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var health = await _client!.CallToolAsync(
            "diagnostics_health",
            cancellationToken: cancellation.Token);
        var accounts = await _client.CallToolAsync(
            "account_list",
            cancellationToken: cancellation.Token);
        var invalidPut = await _client.CallToolAsync(
            "account_profile_put",
            new Dictionary<string, object?>
            {
                ["profile"] = new Dictionary<string, object?>
                {
                    ["id"] = string.Empty,
                    ["display_name"] = "private-profile-value",
                    ["username"] = "private-user-value",
                    ["authentication"] = "password",
                    ["imap"] = new Dictionary<string, object?>
                    {
                        ["host"] = "imap.example.test",
                        ["port"] = 993,
                        ["tls"] = "implicit_tls"
                    },
                    ["pop3"] = null,
                    ["smtp"] = null
                }
            },
            cancellationToken: cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(health.StructuredContent, Is.Not.Null);
            Assert.That(accounts.StructuredContent, Is.Not.Null);
            Assert.That(invalidPut.StructuredContent, Is.Not.Null);
            Assert.That(invalidPut.StructuredContent!.Value.GetProperty("ok").GetBoolean(), Is.False);
            Assert.That(
                health.StructuredContent!.Value.GetProperty("correlation_id").GetString(),
                Does.Match("^[0-9a-f]{32}$"));
            Assert.That(
                accounts.StructuredContent!.Value.GetProperty("correlation_id").GetString(),
                Does.Match("^[0-9a-f]{32}$"));
            Assert.That(
                invalidPut.StructuredContent.Value.GetProperty("correlation_id").GetString(),
                Does.Match("^[0-9a-f]{32}$"));
            Assert.That(invalidPut.StructuredContent.Value.GetRawText(), Does.Not.Contain("private-profile-value"));
            Assert.That(invalidPut.StructuredContent.Value.GetRawText(), Does.Not.Contain("private-user-value"));
            Assert.That(invalidPut.StructuredContent.Value.GetRawText(), Does.Not.Contain("Exception"));
            Assert.That(
                JsonSerializer.Serialize(invalidPut.Content),
                Does.Not.Contain("private-profile-value"));
            Assert.That(
                JsonSerializer.Serialize(invalidPut.Content),
                Does.Not.Contain("private-user-value"));
            Assert.That(
                string.Join(Environment.NewLine, _standardError),
                Does.Not.Contain("threw an unhandled exception").IgnoreCase);
        });
    }

    [Test]
    public async Task NumericEnumsAreRejectedWithoutPersistenceOrMarkerDisclosure()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var marker = "private-numeric-enum-marker";

        var numericAuthentication = await CallPutAsync(
            "numeric-authentication",
            marker,
            999,
            "implicit_tls",
            cancellation.Token);
        var numericTls = await CallPutAsync(
            "numeric-tls",
            marker,
            "password",
            999,
            cancellation.Token);

        var responseText = JsonSerializer.Serialize(new[]
        {
            numericAuthentication.Content,
            numericTls.Content
        });
        var accountsDirectory = Path.Combine(_dataDirectory!, "accounts");
        var standardError = string.Join(Environment.NewLine, _standardError);

        Assert.Multiple(() =>
        {
            Assert.That(numericAuthentication.IsError, Is.True);
            Assert.That(numericTls.IsError, Is.True);
            Assert.That(
                Directory.Exists(accountsDirectory)
                    ? Directory.EnumerateFiles(accountsDirectory, "*.json").ToArray()
                    : Array.Empty<string>(),
                Is.Empty);
            Assert.That(responseText, Does.Not.Contain(marker));
            Assert.That(standardError, Does.Not.Contain(marker));
        });
    }

    private async Task<ModelContextProtocol.Protocol.CallToolResult> CallPutAsync(
        string id,
        string displayName,
        object authentication,
        object tls,
        CancellationToken cancellationToken) =>
        await _client!.CallToolAsync(
            "account_profile_put",
            new Dictionary<string, object?>
            {
                ["profile"] = new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["display_name"] = displayName,
                    ["username"] = "user@example.test",
                    ["authentication"] = authentication,
                    ["imap"] = new Dictionary<string, object?>
                    {
                        ["host"] = "imap.example.test",
                        ["port"] = 993,
                        ["tls"] = tls
                    },
                    ["pop3"] = null,
                    ["smtp"] = null
                }
            },
            cancellationToken: cancellationToken);

    private static bool ContainsSecretName(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                    ContainsSecretName(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsSecretName);
        }

        return false;
    }

    private static Dictionary<string, string?> CreateServerEnvironment(string dataDirectory)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["MAILKIT_AGENT_DATA_DIR"] = dataDirectory;
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        return environment;
    }

    private static string ResolveServerAssembly()
    {
        var configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory)
            .Parent?.Name ?? "Debug";
        return Path.Combine(
            FindRepositoryRoot(),
            "src",
            "MailKit.Agent.Mcp",
            "bin",
            configuration,
            "net8.0",
            "MailKit.Agent.Mcp.dll");
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
