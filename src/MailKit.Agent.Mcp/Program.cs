using MailKit.Agent.Auth;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mcp;
using MailKit.Agent.Mcp.Cli;

var dataDirectory = AppDataPaths.Resolve();
var store = new JsonAccountProfileStore(dataDirectory);
IAccountCredentialVault vault = OperatingSystem.IsWindows()
	? new WindowsCredentialVault()
	: new UnsupportedCredentialVault();
var credentialCommand = new CredentialCommand(store, vault, new SecretConsole());
var exitCode = await credentialCommand.TryRunAsync(args, CancellationToken.None);
if (exitCode is not null)
	return exitCode.Value;

await McpServerHost.RunAsync(args, dataDirectory, vault);
return 0;
