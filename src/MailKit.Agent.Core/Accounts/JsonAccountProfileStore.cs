using System.Text.Json;

namespace MailKit.Agent.Core.Accounts;

public sealed class JsonAccountProfileStore : IAccountProfileStore
{
    private readonly string _accountsDirectory;

    public JsonAccountProfileStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _accountsDirectory = Path.Combine(dataDirectory, "accounts");
    }

    public async Task<IReadOnlyList<AccountProfile>> ListAsync(
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_accountsDirectory))
            return Array.Empty<AccountProfile>();

        var profiles = new List<AccountProfile>();
        foreach (var path in Directory.EnumerateFiles(_accountsDirectory, "*.json"))
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous);
            var profile = await JsonSerializer.DeserializeAsync<AccountProfile>(
                stream,
                cancellationToken: cancellationToken);
            if (profile is not null)
                profiles.Add(profile);
        }

        return profiles
            .OrderBy(profile => profile.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<AccountProfile?> GetAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var path = GetProfilePath(id);
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync<AccountProfile>(
            stream,
            cancellationToken: cancellationToken);
    }

    public async Task PutAsync(
        AccountProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (AccountProfileValidator.Validate(profile).Count > 0)
            throw new ArgumentException("Account profile is invalid.", nameof(profile));

        var destination = GetProfilePath(profile.Id);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(_accountsDirectory);

        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    profile,
                    cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            await MoveOverDestinationAsync(
                temporary,
                destination,
                cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public Task<bool> DeleteAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var path = GetProfilePath(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return Task.FromResult(false);

        File.Delete(path);
        return Task.FromResult(true);
    }

    private string GetProfilePath(string id)
    {
        if (!AccountProfileValidator.ValidateId(id))
            throw new ArgumentException("Account ID has an invalid format.", nameof(id));

        return Path.Combine(_accountsDirectory, id + ".json");
    }

    private static async Task MoveOverDestinationAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
            }
        }
    }
}
