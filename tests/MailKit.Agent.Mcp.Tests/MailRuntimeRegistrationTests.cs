using System.Reflection;
using MailKit.Agent.Core.Mail;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Connections;
using MailKit.Agent.Mail.Imap;
using MailKit.Agent.Mail.Pop3;
using MailKit.Agent.Mail.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace MailKit.Agent.Mcp.Tests;

/// <summary>
/// Guards the DI wiring that makes the registered <see cref="ConnectionGate"/>
/// singleton the gate every protocol gateway holds leases through, so the
/// per-account/per-protocol/global connection limits are enforced process-wide.
/// </summary>
public sealed class MailRuntimeRegistrationTests
{
    [Test]
    public void RuntimeGatewaysShareTheRegisteredConnectionGate()
    {
        string dataDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            McpServerHost.ConfigureMailRuntime(services, dataDirectory);
            using ServiceProvider provider = services.BuildServiceProvider();

            ConnectionGate gate = provider.GetRequiredService<ConnectionGate>();
            var imap = (ImapGateway)provider.GetRequiredService<IImapGateway>();
            var pop3 = (Pop3Gateway)provider.GetRequiredService<IPop3Gateway>();
            var smtp = (SmtpGateway)provider.GetRequiredService<ISmtpGateway>();

            Assert.Multiple(() =>
            {
                Assert.That(GateOf(imap), Is.SameAs(gate),
                    "ImapGateway must acquire through the registered gate singleton.");
                Assert.That(GateOf(pop3), Is.SameAs(gate),
                    "Pop3Gateway must acquire through the registered gate singleton.");
                Assert.That(GateOf(smtp), Is.SameAs(gate),
                    "SmtpGateway must acquire through the registered gate singleton.");
            });
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static ConnectionGate GateOf(object gateway) =>
        (ConnectionGate)gateway.GetType()
            .GetField("connectionGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(gateway)!;
}
