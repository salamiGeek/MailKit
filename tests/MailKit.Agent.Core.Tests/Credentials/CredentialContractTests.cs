using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Core.Tests.Credentials;

public class CredentialContractTests
{
    [TestCase("personal", "MailKit.Agent/account/personal/password")]
    [TestCase("work_2", "MailKit.Agent/account/work_2/password")]
    public void PasswordTargetUsesStableExactName(string accountId, string expected) =>
        Assert.That(CredentialTarget.Password(accountId), Is.EqualTo(expected));

    [Test]
    public void PasswordLeaseRejectsUseAfterDispose()
    {
        using var lease = PasswordCredentialLease.FromCharacters("app-password".AsSpan());
        Assert.That(
            lease.CreateNetworkCredential("user@example.test").Password,
            Is.EqualTo("app-password"));

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            lease.CreateNetworkCredential("user@example.test"));
    }
}
