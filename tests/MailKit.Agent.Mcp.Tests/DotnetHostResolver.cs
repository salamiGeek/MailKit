namespace MailKit.Agent.Mcp.Tests;

internal static class DotnetHostResolver
{
    public static string Resolve() =>
        Resolve(Environment.GetEnvironmentVariable);

    internal static string Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var configuredPath = getEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(configuredPath) ? "dotnet" : configuredPath;
    }
}
