using System.Text.Json;
using MailKit.Agent.Core.Sending;

namespace MailKit.Agent.Core.Tests.Sending;

public class JsonSendLedgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
    private const string AccountId = "personal";
    private const string KeyHash = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2";
    private const string RawKeyNeverStored = "idem-001";

    [Test]
    public async Task CreateAndFindRoundTripPersistsOnlyAllowedFields()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);

        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        var attempting = await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);
        var succeeded = await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Succeeded, Now.AddSeconds(10), "corr-1", CancellationToken.None);
        var found = await ledger.FindAsync(AccountId, KeyHash, CancellationToken.None);

        var path = Path.Combine(temp.Path, "send-ledger", AccountId, KeyHash + ".json");
        var json = await File.ReadAllTextAsync(path);
        using var document = JsonDocument.Parse(json);
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name);

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(path), Is.True);
            Assert.That(found, Is.EqualTo(succeeded));
            Assert.That(found!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(found.MessageId, Is.EqualTo("<message-1@example.com>"));
            Assert.That(found.PreparedAt, Is.EqualTo(Now));
            Assert.That(found.AttemptedAt, Is.EqualTo(Now.AddSeconds(5)));
            Assert.That(found.CompletedAt, Is.EqualTo(Now.AddSeconds(10)));
            Assert.That(found.CorrelationId, Is.EqualTo("corr-1"));
            Assert.That(attempting.State, Is.EqualTo(SendState.Attempting));
            Assert.That(propertyNames, Is.EquivalentTo(new[]
            {
                "account_id",
                "idempotency_key_hash",
                "message_id",
                "state",
                "prepared_at",
                "attempted_at",
                "completed_at",
                "correlation_id"
            }));
            Assert.That(json, Does.Not.Contain(RawKeyNeverStored));
            Assert.That(json, Does.Not.Contain("subject"));
            Assert.That(json, Does.Not.Contain("recipient"));
            Assert.That(json, Does.Not.Contain("body"));
        });
    }

    [Test]
    public async Task FindReturnsNullWhenNoRecordExists()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);

        var found = await ledger.FindAsync(AccountId, KeyHash, CancellationToken.None);

        Assert.That(found, Is.Null);
    }

    [Test]
    public async Task CreateRejectsReplacingTerminalRecord()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Succeeded, Now.AddSeconds(10), "corr-1", CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.CreateAsync(PreparedEntry() with { MessageId = "<other@example.com>" }, CancellationToken.None));
    }

    [Test]
    public async Task CreateReplacesStalePreparedRecord()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);

        var replacement = PreparedEntry() with { MessageId = "<retry@example.com>" };
        await ledger.CreateAsync(replacement, CancellationToken.None);
        var found = await ledger.FindAsync(AccountId, KeyHash, CancellationToken.None);

        Assert.That(found, Is.EqualTo(replacement));
    }

    [TestCase(SendState.Succeeded)]
    [TestCase(SendState.Failed)]
    [TestCase(SendState.Indeterminate)]
    public async Task AttemptingTransitionsToTerminalStates(SendState terminal)
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);

        var updated = await ledger.TransitionAsync(
            AccountId, KeyHash, terminal, Now.AddSeconds(10), "corr-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(updated.State, Is.EqualTo(terminal));
            Assert.That(updated.CompletedAt, Is.EqualTo(Now.AddSeconds(10)));
        });
    }

    [TestCase(SendState.Prepared, SendState.Succeeded)]
    [TestCase(SendState.Prepared, SendState.Failed)]
    [TestCase(SendState.Prepared, SendState.Indeterminate)]
    [TestCase(SendState.Prepared, SendState.Prepared)]
    [TestCase(SendState.Attempting, SendState.Prepared)]
    [TestCase(SendState.Attempting, SendState.Attempting)]
    [TestCase(SendState.Succeeded, SendState.Attempting)]
    [TestCase(SendState.Succeeded, SendState.Failed)]
    [TestCase(SendState.Succeeded, SendState.Indeterminate)]
    [TestCase(SendState.Failed, SendState.Attempting)]
    [TestCase(SendState.Failed, SendState.Succeeded)]
    [TestCase(SendState.Indeterminate, SendState.Attempting)]
    [TestCase(SendState.Indeterminate, SendState.Succeeded)]
    public async Task TransitionRejectsDisallowedPaths(SendState start, SendState target)
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        if (start != SendState.Prepared)
            await ledger.TransitionAsync(
                AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);
        if (start is SendState.Succeeded or SendState.Failed or SendState.Indeterminate)
            await ledger.TransitionAsync(
                AccountId, KeyHash, start, Now.AddSeconds(10), "corr-1", CancellationToken.None);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.TransitionAsync(AccountId, KeyHash, target, Now.AddSeconds(15), "corr-1", CancellationToken.None));
    }

    [Test]
    public void TransitionRejectsUnknownRecord()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);

        Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.TransitionAsync(
                AccountId, KeyHash, SendState.Attempting, Now, "corr-1", CancellationToken.None));
    }

    [Test]
    public async Task AttemptingFromEarlierProcessLoadsAsIndeterminate()
    {
        using var temp = new TemporaryDirectory();
        var earlier = new JsonSendLedger(temp.Path);
        await earlier.CreateAsync(PreparedEntry(), CancellationToken.None);
        await earlier.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);

        var restarted = new JsonSendLedger(temp.Path);
        var found = await restarted.FindAsync(AccountId, KeyHash, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(found!.State, Is.EqualTo(SendState.Indeterminate));
            Assert.ThrowsAsync<InvalidOperationException>(() => restarted.TransitionAsync(
                AccountId, KeyHash, SendState.Succeeded, Now.AddSeconds(10), "corr-1", CancellationToken.None));
            Assert.ThrowsAsync<InvalidOperationException>(() => restarted.CreateAsync(
                PreparedEntry(), CancellationToken.None));
        });
    }

    [Test]
    public async Task AttemptingStaysLiveForOwningInstanceAndCanComplete()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);

        var found = await ledger.FindAsync(AccountId, KeyHash, CancellationToken.None);
        var completed = await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Succeeded, Now.AddSeconds(10), "corr-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(found!.State, Is.EqualTo(SendState.Attempting));
            Assert.That(completed.State, Is.EqualTo(SendState.Succeeded));
        });
    }

    [Test]
    public async Task WritesAreAtomicAndLeaveNoTemporarySibling()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);

        var directory = Path.Combine(temp.Path, "send-ledger", AccountId);
        var temporaryFiles = Directory.EnumerateFiles(directory, "*.tmp*").ToArray();

        Assert.That(temporaryFiles, Is.Empty);
    }

    [Test]
    public async Task EachLedgerOperationPerformsExactlyOneDurableWrite()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        var directory = Path.Combine(temp.Path, "send-ledger", AccountId);
        Directory.CreateDirectory(directory);
        var temporaryFilesCreated = 0;
        using var watcher = new FileSystemWatcher(directory)
        {
            Filter = "*.tmp",
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };
        watcher.Created += (_, _) => Interlocked.Increment(ref temporaryFilesCreated);

        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-1", CancellationToken.None);
        await ledger.TransitionAsync(
            AccountId, KeyHash, SendState.Succeeded, Now.AddSeconds(10), "corr-1", CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (temporaryFilesCreated < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20, CancellationToken.None);
        await Task.Delay(300, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(temporaryFilesCreated, Is.EqualTo(3),
                "Exactly one durable write per ledger operation: create plus two transitions.");
            Assert.That(Directory.EnumerateFiles(directory, "*.tmp*"), Is.Empty);
        });
    }

    [Test]
    public async Task ConcurrentSameKeyTransitionsAllowExactlyOneAttempting()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitions = Enumerable.Range(1, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            try
            {
                return await ledger.TransitionAsync(
                    AccountId, KeyHash, SendState.Attempting, Now.AddSeconds(5), "corr-race",
                    CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        })).ToArray();

        start.SetResult();
        var outcomes = await Task.WhenAll(transitions);
        var found = await ledger.FindAsync(AccountId, KeyHash, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcomes.Count(entry => entry is not null), Is.EqualTo(1),
                "Only one concurrent transition may enter Attempting.");
            Assert.That(outcomes.Single(entry => entry is not null)!.State, Is.EqualTo(SendState.Attempting));
            Assert.That(outcomes.Count(entry => entry is null), Is.EqualTo(7));
            Assert.That(found!.State, Is.EqualTo(SendState.Attempting));
        });
    }

    [TestCase("../escape")]
    [TestCase("..\\escape")]
    [TestCase("UPPERCASE")]
    [TestCase("a.b")]
    [TestCase("")]
    public void RejectsUnsafeAccountId(string accountId)
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);

        Assert.ThrowsAsync<ArgumentException>(() =>
            ledger.FindAsync(accountId, KeyHash, CancellationToken.None));
        Assert.ThrowsAsync<ArgumentException>(() =>
            ledger.CreateAsync(PreparedEntry() with { AccountId = accountId }, CancellationToken.None));
    }

    [TestCase("A1B2C3D4E5F6A7B8C9D0E1F2A3B4C5D6E7F8A9B0C1D2E3F4A5B6C7D8E9F0A1B2")]
    [TestCase("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b")]
    [TestCase("z1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2")]
    [TestCase("../escape")]
    public void RejectsNonCanonicalIdempotencyHash(string keyHash)
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);

        Assert.ThrowsAsync<ArgumentException>(() =>
            ledger.FindAsync(AccountId, keyHash, CancellationToken.None));
    }

    [Test]
    public void RejectsNonPreparedCreateEntries()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<ArgumentException>(() => ledger.CreateAsync(
                PreparedEntry() with { State = SendState.Succeeded }, CancellationToken.None));
            Assert.ThrowsAsync<ArgumentNullException>(() =>
                ledger.CreateAsync(null!, CancellationToken.None));
        });
    }

    [Test]
    public async Task LedgerStoresMultipleAccountsIndependently()
    {
        using var temp = new TemporaryDirectory();
        var ledger = new JsonSendLedger(temp.Path);
        var otherHash = "b1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1b2";

        await ledger.CreateAsync(PreparedEntry(), CancellationToken.None);
        await ledger.CreateAsync(PreparedEntry() with
        {
            AccountId = "work",
            IdempotencyKeyHash = otherHash
        }, CancellationToken.None);

        var personal = await ledger.FindAsync(AccountId, KeyHash, CancellationToken.None);
        var work = await ledger.FindAsync("work", otherHash, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(personal!.AccountId, Is.EqualTo(AccountId));
            Assert.That(work!.AccountId, Is.EqualTo("work"));
        });
    }

    private static SendLedgerEntry PreparedEntry() => new(
        AccountId,
        KeyHash,
        "<message-1@example.com>",
        SendState.Prepared,
        Now,
        null,
        null,
        null);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mailkit-agent-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
