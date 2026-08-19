using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Tests.Applications;

public class ConnectionApplicationTests
{
    [Test]
    public async Task TestReturnsEveryRequestedProtocolWhenOneFails()
    {
        var tester = new FakeTester
        {
            Errors = new Dictionary<string, ToolError>
            {
                ["imap"] = new("connection.tls_failed", ErrorCategory.Authentication,
                    "TLS negotiation failed.", false, null, null)
            }
        };
        var app = CreateApplication(tester);

        ToolResult<IReadOnlyList<ProtocolConnectionResult>> result = await app.TestAsync(
            "personal", ["imap", "pop3", "smtp"], CancellationToken.None);

        IReadOnlyList<ProtocolConnectionResult> data = result.Data!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(data.Select(item => item.Protocol), Is.EqualTo(new[] { "imap", "pop3", "smtp" }));
            Assert.That(data[0].Connected, Is.False);
            Assert.That(data[0].Error!.Code, Is.EqualTo("connection.tls_failed"));
            Assert.That(data[1].Authenticated, Is.True);
            Assert.That(data[2].Capabilities, Is.EqualTo(new[] { "smtp-capability" }));
            Assert.That(tester.Calls, Is.EqualTo(new[] { "imap", "pop3", "smtp" }));
        });
    }

    [Test]
    public async Task TestReportsUnconfiguredRequestedProtocolWithoutCallingTester()
    {
        var tester = new FakeTester();
        var profile = Profile() with { Pop3 = null };
        var app = CreateApplication(tester, profile);

        ToolResult<IReadOnlyList<ProtocolConnectionResult>> result = await app.TestAsync(
            "personal", ["pop3", "imap"], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data![0].Error!.Code, Is.EqualTo("pop3.not_configured"));
            Assert.That(result.Data[1].Connected, Is.True);
            Assert.That(tester.Calls, Is.EqualTo(new[] { "imap" }));
        });
    }

    [Test]
    public async Task TestWithNoProtocolSelectionUsesConfiguredProtocolsOnly()
    {
        var tester = new FakeTester();
        var profile = Profile() with { Pop3 = null };
        var app = CreateApplication(tester, profile);

        ToolResult<IReadOnlyList<ProtocolConnectionResult>> result = await app.TestAsync(
            "personal", protocols: null, CancellationToken.None);

        Assert.That(result.Data!.Select(item => item.Protocol), Is.EqualTo(new[] { "imap", "smtp" }));
    }

    private static ConnectionApplication CreateApplication(FakeTester tester, AccountProfile? profile = null) =>
        new(
            new FakeStore { Profile = profile ?? Profile() },
            new FakeVault(),
            tester,
            OperationPolicy.Default);

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.com", AuthenticationKind.Password,
        new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
        new EndpointSettings("pop.example.com", 995, TlsMode.ImplicitTls),
        new EndpointSettings("smtp.example.com", 465, TlsMode.ImplicitTls));

    private sealed class FakeTester : IProtocolConnectionTester
    {
        public IReadOnlyDictionary<string, ToolError> Errors { get; init; } = new Dictionary<string, ToolError>();
        public List<string> Calls { get; } = [];
        public Task<ProtocolConnectionResult> TestAsync(string protocol, AccountProfile profile, PasswordCredentialLease credential, CancellationToken cancellationToken)
        {
            Calls.Add(protocol);
            if (Errors.TryGetValue(protocol, out ToolError? error))
                throw new MailOperationException(error);
            return Task.FromResult(new ProtocolConnectionResult(
                protocol, true, true, true, [$"{protocol}-capability"], null));
        }
    }

    private sealed class FakeStore : IAccountProfileStore
    {
        public AccountProfile? Profile { get; init; }
        public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken) => Task.FromResult(Profile);
        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<AccountProfile>>([]);
        public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeVault : IAccountCredentialVault
    {
        public ValueTask<CredentialStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(new CredentialStatus(true, CredentialKind.Password));
        public ValueTask<PasswordCredentialLease> GetPasswordAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(PasswordCredentialLease.FromCharacters("private-password"));
        public ValueTask SetPasswordAsync(string accountId, string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> DeletePasswordAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
