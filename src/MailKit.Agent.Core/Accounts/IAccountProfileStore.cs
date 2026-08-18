namespace MailKit.Agent.Core.Accounts;

public interface IAccountProfileStore
{
    Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken);
    Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken);
    Task PutAsync(AccountProfile profile, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken);
}
