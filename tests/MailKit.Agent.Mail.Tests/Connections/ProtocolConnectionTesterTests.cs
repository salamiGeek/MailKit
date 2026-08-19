using System.Reflection;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Mail.Connections;

namespace MailKit.Agent.Mail.Tests.Connections;

public class ProtocolConnectionTesterTests
{
    [Test]
    public async Task TestReportsConnectionStateAndCapabilitiesThenCleansUp()
    {
        var proxy = MailServiceProxy.Create();
        var tester = new ProtocolConnectionTester(
            (protocol, profile, credential, cancellationToken) =>
                Task.FromResult(proxy.Service),
            service => ["idle", "uid-plus"]);
        using var credential = PasswordCredentialLease.FromCharacters("private-password");

        var result = await tester.TestAsync(
            "imap", Profile(), credential, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Protocol, Is.EqualTo("imap"));
            Assert.That(result.Connected, Is.True);
            Assert.That(result.TlsEstablished, Is.True);
            Assert.That(result.Authenticated, Is.True);
            Assert.That(result.Capabilities, Is.EqualTo(new[] { "idle", "uid-plus" }));
            Assert.That(result.Error, Is.Null);
            Assert.That(proxy.DisconnectCalls, Is.EqualTo(1));
            Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CleanupFailureDoesNotReplaceSuccessfulConnectionResult()
    {
        var proxy = MailServiceProxy.Create();
        proxy.DisconnectFailure = new IOException("cleanup marker");
        var tester = new ProtocolConnectionTester(
            (protocol, profile, credential, cancellationToken) =>
                Task.FromResult(proxy.Service),
            service => Array.Empty<string>());
        using var credential = PasswordCredentialLease.FromCharacters("private-password");

        var result = await tester.TestAsync(
            "smtp", Profile(), credential, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Connected, Is.True);
            Assert.That(result.Error, Is.Null);
            Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
        });
    }

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.com", AuthenticationKind.Password,
        new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
        new EndpointSettings("pop.example.com", 995, TlsMode.ImplicitTls),
        new EndpointSettings("smtp.example.com", 465, TlsMode.ImplicitTls));

    private class MailServiceProxy : DispatchProxy
    {
        public IMailService Service { get; private set; } = null!;
        public int DisconnectCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Exception? DisconnectFailure { get; set; }

        public static MailServiceProxy Create()
        {
            var service = DispatchProxy.Create<IMailService, MailServiceProxy>();
            var proxy = (MailServiceProxy)(object)service;
            proxy.Service = service;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_IsConnected":
                    return true;
                case "get_IsSecure":
                    return true;
                case "get_IsAuthenticated":
                    return true;
                case nameof(IMailService.DisconnectAsync):
                    DisconnectCalls++;
                    return DisconnectFailure is null
                        ? Task.CompletedTask
                        : Task.FromException(DisconnectFailure);
                case nameof(IDisposable.Dispose):
                    DisposeCalls++;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
