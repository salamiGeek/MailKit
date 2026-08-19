using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Credentials;

public static class CredentialTarget
{
    public static string Password(string accountId)
    {
        if (!AccountProfileValidator.ValidateId(accountId))
            throw new ArgumentException("Account ID has an invalid format.", nameof(accountId));

        return $"MailKit.Agent/account/{accountId}/password";
    }
}
