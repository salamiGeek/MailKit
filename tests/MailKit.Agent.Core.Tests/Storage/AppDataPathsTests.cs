using MailKit.Agent.Core.Storage;

namespace MailKit.Agent.Core.Tests.Storage;

[NonParallelizable]
public class AppDataPathsTests
{
    [Test]
    public void PluginDataTakesPrecedence()
    {
        WithDataEnvironment(
            pluginData: "plugin-data",
            agentData: "agent-data",
            assertion: () => Assert.That(AppDataPaths.Resolve(), Is.EqualTo("plugin-data")));
    }

    [Test]
    public void AgentDataIsUsedWhenPluginDataIsEmpty()
    {
        WithDataEnvironment(
            pluginData: " ",
            agentData: "agent-data",
            assertion: () => Assert.That(AppDataPaths.Resolve(), Is.EqualTo("agent-data")));
    }

    [Test]
    public void LocalApplicationDataIsTheFallback()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailKit.Agent");

        WithDataEnvironment(
            pluginData: null,
            agentData: "",
            assertion: () => Assert.That(AppDataPaths.Resolve(), Is.EqualTo(expected)));
    }

    private static void WithDataEnvironment(
        string? pluginData,
        string? agentData,
        TestDelegate assertion)
    {
        const string pluginDataName = "PLUGIN_DATA";
        const string agentDataName = "MAILKIT_AGENT_DATA_DIR";
        var originalPluginData = Environment.GetEnvironmentVariable(pluginDataName);
        var originalAgentData = Environment.GetEnvironmentVariable(agentDataName);

        try
        {
            Environment.SetEnvironmentVariable(pluginDataName, pluginData);
            Environment.SetEnvironmentVariable(agentDataName, agentData);
            assertion();
        }
        finally
        {
            Environment.SetEnvironmentVariable(pluginDataName, originalPluginData);
            Environment.SetEnvironmentVariable(agentDataName, originalAgentData);
        }
    }
}
