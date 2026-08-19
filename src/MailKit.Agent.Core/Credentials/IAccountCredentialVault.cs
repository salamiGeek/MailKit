namespace MailKit.Agent.Core.Credentials;

public interface IAccountCredentialVault
{
    ValueTask<CredentialStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken);

    ValueTask<PasswordCredentialLease> GetPasswordAsync(
        string accountId,
        CancellationToken cancellationToken);

    ValueTask SetPasswordAsync(
        string accountId,
        string username,
        ReadOnlyMemory<char> password,
        CancellationToken cancellationToken);

    ValueTask<bool> DeletePasswordAsync(string accountId, CancellationToken cancellationToken);
}
