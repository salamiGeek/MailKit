namespace MailKit.Agent.Core.Storage;

public static class AppDataPaths
{
    public static string Resolve()
    {
        var pluginData = Environment.GetEnvironmentVariable("PLUGIN_DATA");
        if (!string.IsNullOrWhiteSpace(pluginData))
            return pluginData;

        var agentData = Environment.GetEnvironmentVariable("MAILKIT_AGENT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(agentData))
            return agentData;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailKit.Agent");
    }
}
