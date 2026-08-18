using System.Text.RegularExpressions;

namespace MailKit.Agent.Core.Accounts;

public static class AccountProfileValidator
{
    public const int MaxDisplayNameLength = 256;
    public const int MaxUsernameLength = 320;
    public const int MaxEndpointHostLength = 253;

    private static readonly Regex IdPattern =
        new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Validate(AccountProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<string>();

        if (!IdPattern.IsMatch(profile.Id))
            issues.Add("id: invalid format");
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            issues.Add("display_name: required");
        else if (profile.DisplayName.Length > MaxDisplayNameLength)
            issues.Add($"display_name: must be {MaxDisplayNameLength} characters or fewer");
        if (string.IsNullOrWhiteSpace(profile.Username))
            issues.Add("username: required");
        else if (profile.Username.Length > MaxUsernameLength)
            issues.Add($"username: must be {MaxUsernameLength} characters or fewer");
        if (!Enum.IsDefined(profile.Authentication))
            issues.Add("authentication: invalid value");
        if (profile.Imap is null && profile.Pop3 is null && profile.Smtp is null)
            issues.Add("endpoints: at least one endpoint is required");

        ValidateEndpoint("imap", profile.Imap, issues);
        ValidateEndpoint("pop3", profile.Pop3, issues);
        ValidateEndpoint("smtp", profile.Smtp, issues);
        return issues;
    }

    internal static bool ValidateId(string id) =>
        id is not null && IdPattern.IsMatch(id);

    private static void ValidateEndpoint(
        string field,
        EndpointSettings? endpoint,
        ICollection<string> issues)
    {
        if (endpoint is null)
            return;

        if (string.IsNullOrWhiteSpace(endpoint.Host))
            issues.Add($"{field}.host: required");
        else if (endpoint.Host.Length > MaxEndpointHostLength)
            issues.Add($"{field}.host: must be {MaxEndpointHostLength} characters or fewer");
        if (endpoint.Port is < 1 or > 65535)
            issues.Add($"{field}.port: must be between 1 and 65535");
        if (!Enum.IsDefined(endpoint.Tls))
            issues.Add($"{field}.tls: invalid value");
        else if (endpoint.Tls is TlsMode.Plain)
            issues.Add($"{field}.tls: TLS is required");
    }
}
