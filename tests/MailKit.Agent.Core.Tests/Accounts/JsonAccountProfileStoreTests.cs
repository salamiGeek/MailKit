using System.Reflection;
using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Tests.Accounts;

public class JsonAccountProfileStoreTests
{
    [Test]
    public async Task PutAndGetRoundTripNeverPersistsSecretFields()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        var profile = CreateProfile("work");

        await store.PutAsync(profile, CancellationToken.None);

        var json = await File.ReadAllTextAsync(
            Path.Combine(temp.Path, "accounts", "work.json"));
        var stored = await store.GetAsync("work", CancellationToken.None);
        var listed = (await store.ListAsync(CancellationToken.None)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.EqualTo(profile));
            Assert.That(listed, Is.EqualTo(profile));
            Assert.That(json, Does.Contain("\"display_name\":\"Work\""));
            Assert.That(json, Does.Contain("\"authentication\":\"password\""));
            Assert.That(json, Does.Contain("\"tls\":\"implicit_tls\""));
            Assert.That(json, Does.Not.Contain("\"password\":").IgnoreCase);
            Assert.That(json, Does.Not.Contain("\"token\":").IgnoreCase);
            Assert.That(json, Does.Not.Contain("\"secret\":").IgnoreCase);
        });
    }

    [Test]
    public void AccountProfileContractContainsNoSecretMembers()
    {
        var forbiddenNames = new[] { "password", "token", "secret" };
        var memberNames = typeof(AccountProfile)
            .GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
            .Select(member => member.Name)
            .ToArray();

        Assert.That(memberNames, Has.None.Matches<string>(name =>
            forbiddenNames.Any(forbidden =>
                name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))));
    }

    [Test]
    public async Task GetReturnsNullWhenProfileDoesNotExist()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);

        var profile = await store.GetAsync("missing", CancellationToken.None);

        Assert.That(profile, Is.Null);
    }

    [Test]
    public async Task ListReturnsProfilesInOrdinalIdOrder()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        await store.PutAsync(CreateProfile("a_1"), CancellationToken.None);
        await store.PutAsync(CreateProfile("a1"), CancellationToken.None);
        await store.PutAsync(CreateProfile("a-1"), CancellationToken.None);

        var profiles = await store.ListAsync(CancellationToken.None);

        Assert.That(profiles.Select(profile => profile.Id),
            Is.EqualTo(new[] { "a-1", "a1", "a_1" }));
    }

    [Test]
    public async Task DeleteRemovesExistingProfileAndReportsWhetherItExisted()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        await store.PutAsync(CreateProfile("work"), CancellationToken.None);

        var firstDelete = await store.DeleteAsync("work", CancellationToken.None);
        var secondDelete = await store.DeleteAsync("work", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstDelete, Is.True);
            Assert.That(secondDelete, Is.False);
            Assert.That(File.Exists(Path.Combine(temp.Path, "accounts", "work.json")), Is.False);
        });
    }

    [TestCase("../escape")]
    [TestCase("..\\escape")]
    [TestCase("UPPERCASE")]
    [TestCase("a.b")]
    [TestCase("")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void GetRejectsUnsafeAccountId(string id)
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);

        Assert.ThrowsAsync<ArgumentException>(() =>
            store.GetAsync(id, CancellationToken.None));
    }

    [Test]
    public void PutRejectsUnsafeAccountId()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);

        Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync(CreateProfile("../escape"), CancellationToken.None));
    }

    [Test]
    public void PutRejectsPlainTlsBeforeCreatingStorageArtifacts()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        var profile = CreateProfile("work") with
        {
            Imap = new EndpointSettings("private-imap.example.com", 143, TlsMode.Plain)
        };

        var exception = Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync(profile, CancellationToken.None));

        var accountsDirectory = Path.Combine(temp.Path, "accounts");
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.StartWith("Account profile is invalid."));
            Assert.That(exception.Message, Does.Not.Contain("private-imap.example.com"));
            Assert.That(Directory.Exists(accountsDirectory), Is.False);
            Assert.That(File.Exists(Path.Combine(accountsDirectory, "work.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(accountsDirectory, "work.json.tmp")), Is.False);
        });
    }

    [Test]
    public void PutRejectsOtherInvalidProfilesBeforeCreatingStorageArtifacts()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        var profile = CreateProfile("work") with { DisplayName = " " };

        var exception = Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync(profile, CancellationToken.None));

        var accountsDirectory = Path.Combine(temp.Path, "accounts");
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.StartWith("Account profile is invalid."));
            Assert.That(Directory.Exists(accountsDirectory), Is.False);
            Assert.That(File.Exists(Path.Combine(accountsDirectory, "work.json")), Is.False);
            Assert.That(File.Exists(Path.Combine(accountsDirectory, "work.json.tmp")), Is.False);
        });
    }

    [Test]
    public void PutRejectsUndefinedEnumsBeforeCreatingStorageArtifacts()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        var profile = CreateProfile("work") with
        {
            Authentication = (AuthenticationKind)999,
            Imap = new EndpointSettings("imap.example.com", 993, (TlsMode)999)
        };

        var exception = Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync(profile, CancellationToken.None));

        var accountsDirectory = Path.Combine(temp.Path, "accounts");
        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.StartWith("Account profile is invalid."));
            Assert.That(exception.Message, Does.Not.Contain("999"));
            Assert.That(Directory.Exists(accountsDirectory), Is.False);
        });
    }

    [Test]
    public void DeleteRejectsUnsafeAccountId()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);

        Assert.ThrowsAsync<ArgumentException>(() =>
            store.DeleteAsync("../escape", CancellationToken.None));
    }

    [Test]
    public async Task SuccessfulPutLeavesNoTemporarySibling()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);

        await store.PutAsync(CreateProfile("work"), CancellationToken.None);

        Assert.That(
            File.Exists(Path.Combine(temp.Path, "accounts", "work.json.tmp")),
            Is.False);
    }

    [Test]
    public void FailedPutDeletesOnlyItsExactTemporarySibling()
    {
        using var temp = new TemporaryDirectory();
        var accountsDirectory = Path.Combine(temp.Path, "accounts");
        Directory.CreateDirectory(accountsDirectory);
        Directory.CreateDirectory(Path.Combine(accountsDirectory, "work.json"));
        var unrelatedTemporary = Path.Combine(accountsDirectory, "other.json.tmp");
        File.WriteAllText(unrelatedTemporary, "preserve me");
        var store = new JsonAccountProfileStore(temp.Path);

        Assert.CatchAsync<Exception>(() =>
            store.PutAsync(CreateProfile("work"), CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Combine(accountsDirectory, "work.json.tmp")), Is.False);
            Assert.That(File.ReadAllText(unrelatedTemporary), Is.EqualTo("preserve me"));
        });
    }

    [Test]
    public async Task ConcurrentSameIdPutsUseIndependentTemporaryFiles()
    {
        using var temp = new TemporaryDirectory();
        var store = new JsonAccountProfileStore(temp.Path);
        var profiles = Enumerable.Range(1, 32)
            .Select(index => CreateProfile("work") with
            {
                DisplayName = $"Writer {index}"
            })
            .ToArray();
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = profiles.Select(profile => Task.Run(async () =>
        {
            await start.Task;
            await store.PutAsync(profile, CancellationToken.None);
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(writes);

        var destination = Path.Combine(temp.Path, "accounts", "work.json");
        var finalProfile = await store.GetAsync("work", CancellationToken.None);
        var temporaryFiles = Directory.EnumerateFiles(
            Path.GetDirectoryName(destination)!, "*.tmp*").ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(writes, Has.All.Matches<Task>(task =>
                task.IsCompletedSuccessfully));
            Assert.That(finalProfile, Is.Not.Null);
            Assert.That(profiles, Has.Some.EqualTo(finalProfile));
            Assert.That(temporaryFiles, Is.Empty);
        });
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

public class AccountProfileValidatorTests
{
    [TestCase("account")]
    [TestCase("a")]
    [TestCase("a-1_b")]
    [TestCase("a123456789012345678901234567890123456789012345678901234567890123")]
    public void AcceptsValidAccountIds(string id)
    {
        var issues = AccountProfileValidator.Validate(CreateProfile(id));

        Assert.That(issues, Has.No.Member("id: invalid format"));
    }

    [TestCase("")]
    [TestCase("Upper")]
    [TestCase("-leading")]
    [TestCase("under space")]
    [TestCase("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RejectsInvalidAccountIds(string id)
    {
        var issues = AccountProfileValidator.Validate(CreateProfile(id));

        Assert.That(issues, Has.Member("id: invalid format"));
    }

    [Test]
    public void RequiresDisplayNameAndUsername()
    {
        var profile = CreateProfile("work") with
        {
            DisplayName = " ",
            Username = null!
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Is.SupersetOf(new[]
        {
            "display_name: required",
            "username: required"
        }));
    }

    [Test]
    public void RejectsOversizedNonSecretStringsWithStableIssues()
    {
        var profile = CreateProfile("work") with
        {
            DisplayName = new string('d', 257),
            Username = new string('u', 321),
            Imap = new EndpointSettings(new string('h', 254), 993, TlsMode.ImplicitTls)
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Is.EqualTo(new[]
        {
            "display_name: must be 256 characters or fewer",
            "username: must be 320 characters or fewer",
            "imap.host: must be 253 characters or fewer"
        }));
    }

    [Test]
    public void RequiresAtLeastOneEndpoint()
    {
        var profile = CreateProfile("work") with
        {
            Imap = null,
            Pop3 = null,
            Smtp = null
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Has.Member("endpoints: at least one endpoint is required"));
    }

    [Test]
    public void ValidatesEveryEndpointHostPortAndTls()
    {
        var profile = CreateProfile("work") with
        {
            Imap = new EndpointSettings(" ", 0, TlsMode.Plain),
            Pop3 = new EndpointSettings(null!, 65536, TlsMode.Plain),
            Smtp = new EndpointSettings("smtp.example.com", -1, TlsMode.Plain)
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Is.EqualTo(new[]
        {
            "imap.host: required",
            "imap.port: must be between 1 and 65535",
            "imap.tls: TLS is required",
            "pop3.host: required",
            "pop3.port: must be between 1 and 65535",
            "pop3.tls: TLS is required",
            "smtp.port: must be between 1 and 65535",
            "smtp.tls: TLS is required"
        }));
    }

    [TestCase(1)]
    [TestCase(65535)]
    public void AcceptsEndpointPortBoundaries(int port)
    {
        var profile = CreateProfile("work") with
        {
            Imap = new EndpointSettings("imap.example.com", port, TlsMode.StartTls)
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Is.Empty);
    }

    [Test]
    public void RejectsUndefinedAuthenticationKindWithoutEchoingItsValue()
    {
        var profile = CreateProfile("work") with
        {
            Authentication = (AuthenticationKind)999
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Is.EqualTo(new[] { "authentication: invalid value" }));
        Assert.That(string.Join(Environment.NewLine, issues), Does.Not.Contain("999"));
    }

    [Test]
    public void RejectsUndefinedTlsModeForEveryEndpointWithoutEchoingItsValue()
    {
        var profile = CreateProfile("work") with
        {
            Imap = new EndpointSettings("imap.example.com", 993, (TlsMode)999),
            Pop3 = new EndpointSettings("pop3.example.com", 995, (TlsMode)999),
            Smtp = new EndpointSettings("smtp.example.com", 465, (TlsMode)999)
        };

        var issues = AccountProfileValidator.Validate(profile);

        Assert.That(issues, Is.EqualTo(new[]
        {
            "imap.tls: invalid value",
            "pop3.tls: invalid value",
            "smtp.tls: invalid value"
        }));
        Assert.That(string.Join(Environment.NewLine, issues), Does.Not.Contain("999"));
    }

    [Test]
    public void RejectsNullProfile()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AccountProfileValidator.Validate(null!));
    }

    private static AccountProfile CreateProfile(string id) =>
        new(
            id,
            "Work",
            "user@example.com",
            AuthenticationKind.OAuth2,
            new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
            null,
            null);
}
