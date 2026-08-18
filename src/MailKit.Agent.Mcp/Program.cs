using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Serialization;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

var dataDirectory = AppDataPaths.Resolve();
var toolSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
};
toolSerializerOptions.Converters.Add(new LowerSnakeCaseEnumConverter<AuthenticationKind>());
toolSerializerOptions.Converters.Add(new LowerSnakeCaseEnumConverter<TlsMode>());
builder.Services.AddSingleton<IAccountProfileStore>(
    _ => new JsonAccountProfileStore(dataDirectory));
builder.Services.AddSingleton(OperationPolicy.Default);
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DiagnosticsTools>(toolSerializerOptions)
    .WithTools<AccountTools>(toolSerializerOptions);

await builder.Build().RunAsync();
