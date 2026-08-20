using MailKit.Agent.Core.Contracts;

namespace MailKit.Agent.Core.Tests.Contracts;

public class ServerInfoTests
{
    [Test]
    public void FoundationServerIsLocalStdioOnly()
    {
        var info = ServerInfo.Foundation;

        Assert.Multiple(() =>
        {
            Assert.That(info.Name, Is.EqualTo("mailkit-agent"));
            Assert.That(info.Version, Is.EqualTo("0.2.1"));
            Assert.That(info.Transport, Is.EqualTo("stdio"));
            Assert.That(info.NetworkListenerEnabled, Is.False);
        });
    }
}
