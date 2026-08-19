using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Applications;
using MailKit.Agent.Core.Connections;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Mcp.Tools;

namespace MailKit.Agent.Mcp.Tests.Tools;

public class ConnectionToolsTests
{
    [Test]
    public async Task TestForwardsAccountAndProtocolSubsetExactly()
    {
        var tester = new RecordingProtocolTester();
        var application = CreateApplication(tester, CreateProfile("work", withPop3: false));

        var result = await ConnectionTools.TestAsync(
            new ConnectionTestRequest("work", ["imap"]),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(tester.Calls, Has.Count.EqualTo(1));
            Assert.That(tester.Calls[0].Protocol, Is.EqualTo("imap"));
            Assert.That(tester.Calls[0].Username, Is.EqualTo("user@example.test"));
            Assert.That(result.Data, Has.Count.EqualTo(1));
            Assert.That(result.Data![0].Protocol, Is.EqualTo("imap"));
            Assert.That(result.Data[0].Connected, Is.True);
            Assert.That(result.Data[0].Authenticated, Is.True);
        });
    }

    [Test]
    public async Task TestDefaultsToEveryConfiguredProtocolWhenSubsetOmitted()
    {
        var tester = new RecordingProtocolTester();
        var application = CreateApplication(tester, CreateProfile("work", withPop3: false));

        var result = await ConnectionTools.TestAsync(
            new ConnectionTestRequest("work"),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(
                tester.Calls.Select(call => call.Protocol),
                Is.EqualTo(new[] { "imap", "smtp" }));
        });
    }

    [Test]
    public async Task TestMapsPolicyDenialToSanitizedEnvelope()
    {
        const string sensitiveMarker = "private-connection-marker";
        var tester = new RecordingProtocolTester
        {
            Result = new ProtocolConnectionResult(
                "imap", true, true, true, [sensitiveMarker], null)
        };
        var application = CreateApplication(
            tester,
            CreateProfile("work", withPop3: false),
            new OperationPolicy(new PolicyLimits(500, 32)));

        var result = await ConnectionTools.TestAsync(
            new ConnectionTestRequest("work"),
            application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo("policy.output_limit_exceeded"));
            Assert.That(result.Error.Category, Is.EqualTo(ErrorCategory.Policy));
            var json = JsonSerializer.Serialize(result);
            Assert.That(json, Does.Not.Contain(sensitiveMarker));
        });
    }

    [Test]
    public void TestPropagatesCancellation()
    {
        var tester = new RecordingProtocolTester
        {
            Exception = new OperationCanceledException("cancellation-marker")
        };
        var application = CreateApplication(tester, CreateProfile("work", withPop3: false));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => ConnectionTools.TestAsync(
            new ConnectionTestRequest("work", ["imap"]),
            application,
            cancellation.Token));
    }

    [Test]
    public async Task CredentialStatusReportsConfiguredKindWithoutSecretValues()
    {
        var result = await AccountTools.CredentialStatusAsync(
            new AccountIdRequest("work"),
            new FakeVault(),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data!.Configured, Is.True);
            Assert.That(result.Data.Kind, Is.EqualTo(CredentialKind.Password));
            Assert.That(
                JsonSerializer.Serialize(result),
                Does.Not.Contain("fixture-password"));
        });
    }

    [Test]
    public async Task CredentialStatusRejectsBlankAccountId()
    {
        var result = await AccountTools.CredentialStatusAsync(
            new AccountIdRequest("  "),
            new FakeVault(),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo("account.invalid_id"));
        });
    }

    [Test]
    public async Task CredentialStatusSanitizesVaultFailures()
    {
        const string sensitiveMarker = "private-vault-marker";
        var result = await AccountTools.CredentialStatusAsync(
            new AccountIdRequest("work"),
            new FakeVault { StatusException = new InvalidOperationException(sensitiveMarker) },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo("credential.status_failed"));
            Assert.That(JsonSerializer.Serialize(result), Does.Not.Contain(sensitiveMarker));
        });
    }

    private static ConnectionApplication CreateApplication(
        RecordingProtocolTester tester,
        AccountProfile profile,
        OperationPolicy? policy = null)
    {
        var store = new InMemoryStore();
        store.PutAsync(profile, CancellationToken.None).GetAwaiter().GetResult();
        return new ConnectionApplication(store, new FakeVault(), tester, policy ?? OperationPolicy.Default);
    }

    internal static AccountProfile CreateProfile(string id, bool withPop3 = true) =>
        new(
            id,
            "Work",
            "user@example.test",
            AuthenticationKind.Password,
            new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
            withPop3 ? new EndpointSettings("pop3.example.test", 995, TlsMode.ImplicitTls) : null,
            new EndpointSettings("smtp.example.test", 465, TlsMode.ImplicitTls));

    internal sealed class InMemoryStore : IAccountProfileStore
    {
        private readonly Dictionary<string, AccountProfile> profiles = new();

        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AccountProfile>>(profiles.Values.ToArray());

        public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(profiles.GetValueOrDefault(id));

        public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken)
        {
            profiles[profile.Id] = profile;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(profiles.Remove(id));
    }

    internal sealed class FakeVault : IAccountCredentialVault
    {
        public bool Configured { get; init; } = true;

        public Exception? StatusException { get; init; }

        public ValueTask<CredentialStatus> GetStatusAsync(
            string accountId, CancellationToken cancellationToken) =>
            StatusException is null
                ? ValueTask.FromResult(new CredentialStatus(
                    Configured, Configured ? CredentialKind.Password : null))
                : ValueTask.FromException<CredentialStatus>(StatusException);

        public ValueTask<PasswordCredentialLease> GetPasswordAsync(
            string accountId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PasswordCredentialLease.FromCharacters("fixture-password"));

        public ValueTask SetPasswordAsync(
            string accountId,
            string username,
            ReadOnlyMemory<char> password,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fake vault is read-only.");

        public ValueTask<bool> DeletePasswordAsync(
            string accountId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    internal sealed class RecordingProtocolTester : IProtocolConnectionTester
    {
        public List<(string Protocol, string Username)> Calls { get; } = [];

        public ProtocolConnectionResult Result { get; init; } =
            new("imap", true, true, true, Array.Empty<string>(), null);

        public Exception? Exception { get; init; }

        public Task<ProtocolConnectionResult> TestAsync(
            string protocol,
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken)
        {
            Calls.Add((protocol, profile.Username));
            return Exception is null
                ? Task.FromResult(Result with { Protocol = protocol })
                : Task.FromException<ProtocolConnectionResult>(Exception);
        }
    }
}
