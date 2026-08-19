using MailKit.Agent.Auth;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Mcp.Cli;

public sealed class CredentialCommand
{
	private const int ErrorExitCode = 1;
	private const int UsageExitCode = 2;
	private const int CanceledExitCode = 130;
	private readonly IAccountProfileStore _profileStore;
	private readonly IAccountCredentialVault _vault;
	private readonly ISecretConsole _console;

	public CredentialCommand(
		IAccountProfileStore profileStore,
		IAccountCredentialVault vault,
		ISecretConsole console)
	{
		_profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
		_vault = vault ?? throw new ArgumentNullException(nameof(vault));
		_console = console ?? throw new ArgumentNullException(nameof(console));
	}

	public async Task<int?> TryRunAsync(
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		if (arguments.Count < 2 ||
			!string.Equals(arguments[0], "account", StringComparison.Ordinal) ||
			!string.Equals(arguments[1], "credential", StringComparison.Ordinal))
		{
			return null;
		}

		return await RunAsync(arguments, cancellationToken);
	}

	public async Task<int> RunAsync(
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		if (arguments.Count == 3 && IsHelp(arguments[2]))
		{
			WriteHelp();
			return 0;
		}
		if (arguments.Count < 3 ||
			!string.Equals(arguments[0], "account", StringComparison.Ordinal) ||
			!string.Equals(arguments[1], "credential", StringComparison.Ordinal))
		{
			WriteUsageError();
			return UsageExitCode;
		}

		var operation = arguments[2];
		if (operation is not ("set" or "status" or "delete") ||
			!TryParseAccount(arguments, out var accountId))
		{
			WriteUsageError();
			return UsageExitCode;
		}

		try
		{
			var profile = await _profileStore.GetAsync(accountId, cancellationToken);
			if (profile is null)
			{
				_console.WriteLine("Account profile was not found.");
				return ErrorExitCode;
			}

			return operation switch
			{
				"set" => await SetAsync(profile, cancellationToken),
				"status" => await StatusAsync(accountId, cancellationToken),
				"delete" => await DeleteAsync(accountId, cancellationToken),
				_ => UsageExitCode
			};
		}
		catch (OperationCanceledException)
		{
			_console.WriteLine("Operation canceled.");
			return CanceledExitCode;
		}
		catch (CredentialVaultException exception)
		{
			_console.WriteLine(exception.Code);
			return ErrorExitCode;
		}
	}

	private async Task<int> SetAsync(
		AccountProfile profile,
		CancellationToken cancellationToken)
	{
		using var secret = await _console.ReadSecretAsync("Credential: ", cancellationToken);
		await _vault.SetPasswordAsync(
			profile.Id,
			profile.Username,
			secret.Characters,
			cancellationToken);
		_console.WriteLine("Credential configured.");
		return 0;
	}

	private async Task<int> StatusAsync(
		string accountId,
		CancellationToken cancellationToken)
	{
		var status = await _vault.GetStatusAsync(accountId, cancellationToken);
		_console.WriteLine(status.Configured
			? "Credential is configured."
			: "Credential is not configured.");
		return 0;
	}

	private async Task<int> DeleteAsync(
		string accountId,
		CancellationToken cancellationToken)
	{
		var deleted = await _vault.DeletePasswordAsync(accountId, cancellationToken);
		_console.WriteLine(deleted
			? "Credential deleted."
			: "Credential was not configured.");
		return 0;
	}

	private static bool TryParseAccount(
		IReadOnlyList<string> arguments,
		out string accountId)
	{
		accountId = string.Empty;
		if (arguments.Count != 5 ||
			!string.Equals(arguments[3], "--account", StringComparison.Ordinal) ||
			string.IsNullOrWhiteSpace(arguments[4]))
		{
			return false;
		}

		accountId = arguments[4];
		return true;
	}

	private static bool IsHelp(string argument) =>
		argument is "--help" or "-h";

	private void WriteHelp()
	{
		_console.WriteLine("Usage: mailkit-agent account credential <command> --account <id>");
		_console.WriteLine("Commands: set, status, delete");
		_console.WriteLine("Option: --account <id>");
	}

	private void WriteUsageError() =>
		_console.WriteLine("Invalid credential command. Use account credential --help.");
}
