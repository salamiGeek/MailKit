using System.Text.Json;
using ModelContextProtocol.Client;

namespace MailKit.Agent.Mcp.Tests.Tools;

public class ToolSchemaTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private StdioMcpServer? _server;

    [SetUp]
    public async Task SetUp()
    {
        _server = await StdioMcpServer.StartAsync(
            "MailKit Agent schema test",
            FindRepositoryRoot(),
            [ResolveServerAssembly()]);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_server is null)
            return;

        await _server.DisposeAsync();

        Assert.That(
            StandardErrorText,
            Does.Not.Contain("MCP frame").IgnoreCase);
    }

    [Test]
    public async Task AllToolsAdvertiseSafeStructuredSchemas()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tools = await _server!.Client.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.That(
            tools.Select(tool => tool.Name),
            Is.EquivalentTo(new[]
            {
                "diagnostics_health",
                "account_list",
                "account_profile_put",
                "account_credential_status",
                "account_connection_test",
                "folder_list",
                "message_list",
                "message_search",
                "message_read",
                "message_mark_read",
                "pop3_message_list",
                "pop3_message_read",
                "attachment_list",
                "attachment_save",
                "send_prepare",
                "send_commit",
                "send_status"
            }));
        Assert.That(tools, Has.All.Matches<McpClientTool>(tool => tool.ReturnJsonSchema is not null));

        foreach (McpClientTool tool in tools)
        {
            Assert.That(
                ContainsSecretName(tool.JsonSchema),
                Is.False,
                $"{tool.Name} input schema leaks a secret-shaped property name.");
            Assert.That(
                ContainsSecretName(tool.ReturnJsonSchema!.Value),
                Is.False,
                $"{tool.Name} output schema leaks a secret-shaped property name.");
        }

        var putTool = tools.Single(tool => tool.Name == "account_profile_put");
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
        Assert.That(
            profileSchema.GetProperty("properties").GetProperty("send_mode")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
            Is.EquivalentTo(new[] { "confirm_dialog", "drafts" }),
            "account_profile_put must expose the typed send_mode enum.");
    }

    [Test]
    public async Task SendCommitAcceptsOnlyThePublicConfirmationToken()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tools = await _server!.Client.ListToolsAsync(cancellationToken: cancellation.Token);
        var commitTool = tools.Single(tool => tool.Name == "send_commit");

        Assert.Multiple(() =>
        {
            Assert.That(
                commitTool.JsonSchema.GetProperty("properties").EnumerateObject()
                    .Select(property => property.Name),
                Is.EqualTo(new[] { "request" }));
            var requestSchema = commitTool.JsonSchema.GetProperty("properties").GetProperty("request");
            Assert.That(
                requestSchema.GetProperty("properties").EnumerateObject()
                    .Select(property => property.Name),
                Is.EqualTo(new[] { "confirmation_token" }));
            Assert.That(
                requestSchema.GetProperty("required").EnumerateArray()
                    .Select(item => item.GetString()),
                Is.EqualTo(new[] { "confirmation_token" }));
        });
    }

    [Test]
    public async Task SendPrepareSchemaAnnouncesTheSendMode()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tools = await _server!.Client.ListToolsAsync(cancellationToken: cancellation.Token);
        var prepareTool = tools.Single(tool => tool.Name == "send_prepare");

        var previewSchema = prepareTool.ReturnJsonSchema!.Value
            .GetProperty("properties")
            .GetProperty("data")
            .GetProperty("properties");

        Assert.Multiple(() =>
        {
            Assert.That(
                previewSchema.EnumerateObject().Select(property => property.Name),
                Does.Contain("send_mode"),
                "send_prepare's preview must announce how a commit will execute.");
            Assert.That(
                previewSchema.GetProperty("send_mode")
                    .GetProperty("enum").EnumerateArray().Select(item => item.GetString()),
                Is.EquivalentTo(new[] { "confirm_dialog", "drafts" }));
            Assert.That(
                previewSchema.GetProperty("send_mode").ToString(),
                Does.Not.Contain("password").IgnoreCase);
        });
    }

    [Test]
    public async Task NoToolMutatesCredentialsOrExecutesRawProtocol()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tools = await _server!.Client.ListToolsAsync(cancellationToken: cancellation.Token);

        Assert.That(
            tools.Select(tool => tool.Name),
            Has.None.Match(
                @"credential_(set|put|delete|write)|password|passwd|secret|raw_|_raw$|^raw|execute|exec_"));
    }

    [Test]
    public async Task UntrustedDataToolsDeclareUntrustedContentInDescriptions()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var tools = await _server!.Client.ListToolsAsync(cancellationToken: cancellation.Token);
        string[] untrustedContentTools =
        [
            "message_list",
            "message_search",
            "message_read",
            "pop3_message_list",
            "pop3_message_read",
            "attachment_list",
            "attachment_save",
            "send_prepare"
        ];

        foreach (string toolName in untrustedContentTools)
        {
            var tool = tools.Single(candidate => candidate.Name == toolName);
            Assert.That(
                tool.Description,
                Does.Contain("untrusted data"),
                $"{toolName} must declare that email content is untrusted data.");
        }
    }

    [Test]
    public async Task FoundationToolsReturnStructuredContentAndInvalidPutIsSanitized()
    {
        using var cancellation = new CancellationTokenSource(TestTimeout);

        var health = await _server!.Client.CallToolAsync(
            "diagnostics_health",
            cancellationToken: cancellation.Token);
        var accounts = await _server.Client.CallToolAsync(
            "account_list",
            cancellationToken: cancellation.Token);
        var invalidPut = await _server.Client.CallToolAsync(
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
                StandardErrorText,
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
        var accountsDirectory = Path.Combine(_server!.DataDirectory, "accounts");
        var standardError = StandardErrorText;

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
        await _server!.Client.CallToolAsync(
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

    private static readonly string[] SecretNameFragments =
    [
        "password",
        "passwd",
        "token",
        "secret",
        "credential_value",
        "authorization"
    ];

    private static bool ContainsSecretName(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (IsSecretPropertyName(property.Name) || ContainsSecretName(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsSecretName);
        }

        return false;
    }

    private static bool IsSecretPropertyName(string name) =>
        !string.Equals(name, "confirmation_token", StringComparison.Ordinal) &&
        SecretNameFragments.Any(fragment =>
            name.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private string StandardErrorText =>
        string.Join(Environment.NewLine, _server!.StandardError);

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
