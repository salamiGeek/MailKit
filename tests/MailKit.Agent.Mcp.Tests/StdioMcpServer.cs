using System.Collections.Concurrent;
using ModelContextProtocol.Client;

namespace MailKit.Agent.Mcp.Tests;

internal sealed class StdioMcpServer : IAsyncDisposable
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentQueue<string> _standardError = new();

    private StdioMcpServer(string dataDirectory)
    {
        DataDirectory = dataDirectory;
    }

    public McpClient Client { get; private set; } = null!;

    public string DataDirectory { get; }

    public IReadOnlyCollection<string> StandardError => _standardError;

    public static async Task<StdioMcpServer> StartAsync(
        string name,
        string workingDirectory,
        params string[] arguments)
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "mailkit-agent-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var server = new StdioMcpServer(dataDirectory);

        using var cancellation = new CancellationTokenSource(OperationTimeout);
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = DotnetHostResolver.Resolve(),
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            InheritEnvironmentVariables = false,
            EnvironmentVariables = CreateServerEnvironment(dataDirectory),
            StandardErrorLines = server._standardError.Enqueue
        });

        try
        {
            server.Client = await McpClient.CreateAsync(
                transport,
                cancellationToken: cancellation.Token);
            return server;
        }
        catch
        {
            Directory.Delete(dataDirectory, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Client is not null)
            {
                await Client.DisposeAsync()
                    .AsTask()
                    .WaitAsync(OperationTimeout);
            }
        }
        finally
        {
            if (Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }
    }

    private static Dictionary<string, string?> CreateServerEnvironment(string dataDirectory)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["MAILKIT_AGENT_DATA_DIR"] = dataDirectory;
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        return environment;
    }
}
