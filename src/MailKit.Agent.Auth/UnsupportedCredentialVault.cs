using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Auth;

public sealed class UnsupportedCredentialVault : IAccountCredentialVault
{
	public ValueTask<CredentialStatus> GetStatusAsync(
		string accountId,
		CancellationToken cancellationToken) =>
		ValueTask.FromException<CredentialStatus>(CredentialVaultException.PlatformUnsupported());

	public ValueTask<PasswordCredentialLease> GetPasswordAsync(
		string accountId,
		CancellationToken cancellationToken) =>
		ValueTask.FromException<PasswordCredentialLease>(CredentialVaultException.PlatformUnsupported());

	public ValueTask SetPasswordAsync(
		string accountId,
		string username,
		ReadOnlyMemory<char> password,
		CancellationToken cancellationToken) =>
		ValueTask.FromException(CredentialVaultException.PlatformUnsupported());

	public ValueTask<bool> DeletePasswordAsync(
		string accountId,
		CancellationToken cancellationToken) =>
		ValueTask.FromException<bool>(CredentialVaultException.PlatformUnsupported());
}
