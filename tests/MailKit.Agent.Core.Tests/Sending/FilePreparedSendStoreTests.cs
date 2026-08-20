using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Sending;

namespace MailKit.Agent.Core.Tests.Sending;

public class FilePreparedSendStoreTests
{
    [Test]
    public async Task TakeReturnsPreparationAddedByAnotherStoreInstance()
    {
        string directory = CreateDataDirectory();
        var first = new FilePreparedSendStore(directory);
        var second = new FilePreparedSendStore(directory);
        PreparedOutgoingMessage message = Message(expiresInMinutes: 10);

        await first.AddAsync(message, CancellationToken.None);

        PreparedOutgoingMessage? taken = await second.TakeAsync(
            message.PreparationId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(taken, Is.Not.Null,
                "A fresh store instance over the same data directory must see the " +
                "preparation written by the earlier instance (process restart).");
            Assert.That(taken!.PreparationId, Is.EqualTo(message.PreparationId));
            Assert.That(taken.AccountId, Is.EqualTo(message.AccountId));
            Assert.That(taken.MimeMessage, Is.EqualTo(message.MimeMessage));
            Assert.That(taken.Preview.Subject, Is.EqualTo(message.Preview.Subject));
        });
    }

    [Test]
    public async Task TakeRemovesPreparationSoSecondTakeReturnsNull()
    {
        string directory = CreateDataDirectory();
        var store = new FilePreparedSendStore(directory);
        PreparedOutgoingMessage message = Message(expiresInMinutes: 10);
        await store.AddAsync(message, CancellationToken.None);

        PreparedOutgoingMessage? firstTake = await store.TakeAsync(
            message.PreparationId, CancellationToken.None);
        PreparedOutgoingMessage? secondTake = await store.TakeAsync(
            message.PreparationId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstTake, Is.Not.Null);
            Assert.That(secondTake, Is.Null,
                "The one-time preparation must be consumable exactly once, also " +
                "across separate store instances.");
        });
    }

    [Test]
    public async Task TryGetReturnsPreparationWithoutConsumingIt()
    {
        string directory = CreateDataDirectory();
        var store = new FilePreparedSendStore(directory);
        PreparedOutgoingMessage message = Message(expiresInMinutes: 10);
        await store.AddAsync(message, CancellationToken.None);

        PreparedOutgoingMessage? peeked = await store.TryGetAsync(
            message.PreparationId, CancellationToken.None);
        PreparedOutgoingMessage? peekedAgain = await store.TryGetAsync(
            message.PreparationId, CancellationToken.None);
        PreparedOutgoingMessage? taken = await store.TakeAsync(
            message.PreparationId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(peeked, Is.Not.Null);
            Assert.That(peekedAgain, Is.Not.Null,
                "Peeking must never consume the preparation.");
            Assert.That(taken, Is.Not.Null);
        });
    }

    [Test]
    public async Task AccessSweepsExpiredPreparationFiles()
    {
        string directory = CreateDataDirectory();
        var store = new FilePreparedSendStore(directory);
        PreparedOutgoingMessage expired = Message(expiresInMinutes: -1);
        await store.AddAsync(expired, CancellationToken.None);
        string expiredPath = Path.Combine(
            directory, "send-preparations", expired.PreparationId + ".json");
        Assert.That(File.Exists(expiredPath), Is.True);

        PreparedOutgoingMessage? peeked = await store.TryGetAsync(
            expired.PreparationId, CancellationToken.None);
        PreparedOutgoingMessage? taken = await store.TakeAsync(
            expired.PreparationId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(peeked, Is.Null,
                "Expired preparations must be reported as missing.");
            Assert.That(taken, Is.Null);
            Assert.That(File.Exists(expiredPath), Is.False,
                "Expired preparation files must be swept from disk.");
        });
    }

    [Test]
    public async Task AddRejectsPreparationIdsOutsideTheGuidFormat()
    {
        string directory = CreateDataDirectory();
        var store = new FilePreparedSendStore(directory);

        Assert.That(async () => await store.AddAsync(
            Message(preparationId: "../escape", expiresInMinutes: 10),
            CancellationToken.None),
            Throws.TypeOf<ArgumentException>());
        Assert.That(async () => await store.AddAsync(
            Message(preparationId: "not-a-guid", expiresInMinutes: 10),
            CancellationToken.None),
            Throws.TypeOf<ArgumentException>());
        await Task.CompletedTask;
    }

    [Test]
    public async Task PersistedFileNeverContainsTokenShapedPropertyNamesOrValues()
    {
        string directory = CreateDataDirectory();
        var store = new FilePreparedSendStore(directory);
        PreparedOutgoingMessage message = Message(expiresInMinutes: 10);
        message = message with
        {
            Preview = message.Preview with { ConfirmationToken = "token-value-must-not-persist" }
        };

        await store.AddAsync(message, CancellationToken.None);

        string persisted = File.ReadAllText(Path.Combine(
            directory, "send-preparations", message.PreparationId + ".json"));
        Assert.Multiple(() =>
        {
            Assert.That(persisted, Does.Not.Contain("token").IgnoreCase,
                "The one-time confirmation token must never persist: neither its " +
                "value nor a token-shaped property name may appear on disk.");
            Assert.That(persisted, Does.Not.Contain("token-value-must-not-persist"));
        });
    }

    private static string CreateDataDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "mailkit-prepared-store-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static PreparedOutgoingMessage Message(
        string? preparationId = null, int expiresInMinutes = 10)
    {
        string id = preparationId ?? Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var preview = new SendPreview(
            id,
            "personal",
            "<message-1@example.com>",
            null,
            ["Alice <alice@example.com>"],
            [],
            [],
            SendMode.Drafts,
            "Fixture subject",
            "Fixture body preview",
            0,
            [],
            now,
            now.AddMinutes(expiresInMinutes),
            new string('a', 64),
            new string('b', 64),
            string.Empty);

        return new PreparedOutgoingMessage(
            id,
            "personal",
            "<message-1@example.com>",
            preview.ContentHash,
            "fixture-mime"u8.ToArray(),
            null,
            ["alice@example.com"],
            preview,
            preview.IdempotencyKeyHash,
            now.AddMinutes(expiresInMinutes));
    }
}
