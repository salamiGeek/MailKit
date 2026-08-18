using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Mcp.Tools;

namespace MailKit.Agent.Mcp.Tests.Tools;

public class AccountToolsTests
{
    [Test]
    public async Task ListAllowsEmptyResultAtExactSerializedEnvelopeLimit()
    {
        var store = new RecordingStore();
        var expected = ToolResult<IReadOnlyList<AccountProfile>>.Success(
            Array.Empty<AccountProfile>(), new string('0', 32));
        var limit = JsonSerializer.SerializeToUtf8Bytes(expected).Length;

        var result = await AccountTools.ListAsync(
            store,
            new OperationPolicy(new PolicyLimits(500, limit)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data, Is.Empty);
            Assert.That(JsonSerializer.SerializeToUtf8Bytes(result), Has.Length.EqualTo(limit));
        });
    }

    [Test]
    public async Task ListRejectsActualReturnedCountOverPolicyLimit()
    {
        var store = new RecordingStore
        {
            Profiles = [CreateProfile("one"), CreateProfile("two")]
        };

        var result = await AccountTools.ListAsync(
            store,
            new OperationPolicy(new PolicyLimits(1, int.MaxValue)),
            CancellationToken.None);

        AssertPolicyFailure(result, "policy.batch_limit_exceeded");
    }

    [Test]
    public async Task ListRejectsActualSerializedEnvelopeOverPolicyLimit()
    {
        var store = new RecordingStore { Profiles = [CreateProfile("work")] };
        var expected = ToolResult<IReadOnlyList<AccountProfile>>.Success(
            store.Profiles, new string('0', 32));
        var exactSize = JsonSerializer.SerializeToUtf8Bytes(expected).Length;

        var result = await AccountTools.ListAsync(
            store,
            new OperationPolicy(new PolicyLimits(500, exactSize - 1)),
            CancellationToken.None);

        AssertPolicyFailure(result, "policy.output_limit_exceeded");
    }

    [Test]
    public async Task PutRejectsActualSerializedEnvelopeBeforeWriting()
    {
        var profile = CreateProfile("work");
        var store = new RecordingStore();
        var expected = ToolResult<AccountProfile>.Success(profile, new string('0', 32));
        var exactSize = JsonSerializer.SerializeToUtf8Bytes(expected).Length;

        var result = await AccountTools.PutAsync(
            profile,
            store,
            new OperationPolicy(new PolicyLimits(500, exactSize - 1)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertPolicyFailure(result, "policy.output_limit_exceeded");
            Assert.That(store.PutCount, Is.Zero);
        });
    }

    [Test]
    public async Task PutAllowsActualSerializedEnvelopeAtPolicyLimit()
    {
        var profile = CreateProfile("work");
        var store = new RecordingStore();
        var expected = ToolResult<AccountProfile>.Success(profile, new string('0', 32));
        var exactSize = JsonSerializer.SerializeToUtf8Bytes(expected).Length;

        var result = await AccountTools.PutAsync(
            profile,
            store,
            new OperationPolicy(new PolicyLimits(500, exactSize)),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(JsonSerializer.SerializeToUtf8Bytes(result), Has.Length.EqualTo(exactSize));
            Assert.That(store.PutCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ListMapsUnexpectedStoreFailureToSanitizedInternalEnvelope()
    {
        const string sensitiveMarker = "private-path-marker";
        var store = new RecordingStore
        {
            ListException = new InvalidOperationException(sensitiveMarker)
        };

        var result = await AccountTools.ListAsync(
            store,
            OperationPolicy.Default,
            CancellationToken.None);

        AssertSanitizedStoreFailure(result, sensitiveMarker);
    }

    [Test]
    public async Task PutMapsUnexpectedStoreFailureToSanitizedInternalEnvelope()
    {
        const string sensitiveMarker = "private-write-marker";
        var store = new RecordingStore
        {
            PutException = new IOException(sensitiveMarker)
        };

        var result = await AccountTools.PutAsync(
            CreateProfile("work"),
            store,
            OperationPolicy.Default,
            CancellationToken.None);

        AssertSanitizedStoreFailure(result, sensitiveMarker);
    }

    [Test]
    public void ListPropagatesCancellation()
    {
        var store = new RecordingStore
        {
            ListException = new OperationCanceledException("cancellation-marker")
        };

        Assert.ThrowsAsync<OperationCanceledException>(() => AccountTools.ListAsync(
            store,
            OperationPolicy.Default,
            CancellationToken.None));
    }

    [Test]
    public void PutPropagatesCancellation()
    {
        var store = new RecordingStore
        {
            PutException = new OperationCanceledException("cancellation-marker")
        };

        Assert.ThrowsAsync<OperationCanceledException>(() => AccountTools.PutAsync(
            CreateProfile("work"),
            store,
            OperationPolicy.Default,
            CancellationToken.None));
    }

    [Test]
    public async Task ListMapsMalformedStoredJsonToSanitizedInternalEnvelope()
    {
        using var temp = new TemporaryDirectory();
        var accountsDirectory = Path.Combine(temp.Path, "accounts");
        Directory.CreateDirectory(accountsDirectory);
        const string sensitiveMarker = "private-json-marker";
        await File.WriteAllTextAsync(
            Path.Combine(accountsDirectory, "broken.json"),
            "{ not-json: " + sensitiveMarker);

        var result = await AccountTools.ListAsync(
            new JsonAccountProfileStore(temp.Path),
            OperationPolicy.Default,
            CancellationToken.None);

        AssertSanitizedStoreFailure(result, sensitiveMarker);
    }

    private static void AssertPolicyFailure<T>(ToolResult<T> result, string code)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Data, Is.Null);
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo(code));
            Assert.That(result.Error.Category, Is.EqualTo(ErrorCategory.Policy));
            Assert.That(result.Error.Retryable, Is.False);
            Assert.That(result.Error.RetryAfter, Is.Null);
            Assert.That(result.Error.Details, Is.Null);
        });
    }

    private static void AssertSanitizedStoreFailure<T>(
        ToolResult<T> result,
        string sensitiveMarker)
    {
        var json = JsonSerializer.Serialize(result);
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Data, Is.Null);
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(result.Error!.Code, Is.EqualTo("account.store_failure"));
            Assert.That(result.Error.Category, Is.EqualTo(ErrorCategory.Internal));
            Assert.That(result.Error.Message, Is.EqualTo("The account profile store operation failed."));
            Assert.That(result.Error.Retryable, Is.False);
            Assert.That(result.Error.RetryAfter, Is.Null);
            Assert.That(result.Error.Details, Is.Null);
            Assert.That(result.CorrelationId, Has.Length.EqualTo(32));
            Assert.That(json, Does.Not.Contain(sensitiveMarker));
            Assert.That(json, Does.Not.Contain("InvalidOperationException"));
            Assert.That(json, Does.Not.Contain(tempPathFragment()));
        });

        static string tempPathFragment() => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
    }

    private static AccountProfile CreateProfile(string id) =>
        new(
            id,
            "Work",
            "user@example.com",
            AuthenticationKind.Password,
            new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
            null,
            new EndpointSettings("smtp.example.com", 465, TlsMode.ImplicitTls));

    private sealed class RecordingStore : IAccountProfileStore
    {
        public IReadOnlyList<AccountProfile> Profiles { get; init; } = Array.Empty<AccountProfile>();

        public Exception? ListException { get; init; }

        public Exception? PutException { get; init; }

        public int PutCount { get; private set; }

        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) =>
            ListException is null
                ? Task.FromResult(Profiles)
                : Task.FromException<IReadOnlyList<AccountProfile>>(ListException);

        public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<AccountProfile?>(null);

        public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken)
        {
            if (PutException is not null)
                return Task.FromException(PutException);

            PutCount++;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mailkit-agent-mcp-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
