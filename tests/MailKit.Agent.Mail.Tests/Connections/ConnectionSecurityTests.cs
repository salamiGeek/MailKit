using System.Net;
using System.Reflection;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Mail.Connections;
using MailKit.Security;

namespace MailKit.Agent.Mail.Tests.Connections;

public sealed class ConnectionSecurityTests
{
    [TestCase(TlsMode.ImplicitTls, SecureSocketOptions.SslOnConnect)]
    [TestCase(TlsMode.StartTls, SecureSocketOptions.StartTls)]
    public void MapsOnlySecureTlsModes(TlsMode input, SecureSocketOptions expected) =>
        Assert.That(SecureSocketOptionsMapper.Map(input), Is.EqualTo(expected));

    [TestCase(TlsMode.Plain)]
    [TestCase((TlsMode)999)]
    public void RejectsInsecureOrUnknownTlsModes(TlsMode input) =>
        Assert.That(() => SecureSocketOptionsMapper.Map(input),
            Throws.TypeOf<MailOperationException>()
                .With.Property("Error").Property("Code").EqualTo("connection.tls_required"));

    [Test]
    public void UsesRequiredDefaultConnectionLimits()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ConnectionLimits.Default.ConnectTimeout, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(ConnectionLimits.Default.AuthenticateTimeout, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(ConnectionLimits.Default.CommandTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(ConnectionLimits.Default.MaxPerAccountProtocol, Is.EqualTo(2));
            Assert.That(ConnectionLimits.Default.MaxGlobal, Is.EqualTo(8));
        });
    }

    [Test]
    public void RejectsConnectionLimitsOutsideHardCeilings()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new ConnectionLimits(
                TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ConnectionLimits(
                TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ConnectionLimits(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(1), 1, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ConnectionLimits(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(31), 1, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ConnectionLimits(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 3, 1),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ConnectionLimits(
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 9),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public async Task SameAccountProtocolWaitsWhileAnotherAccountCanEnter()
    {
        var gate = new ConnectionGate(new ConnectionLimits(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 3));

        var first = await gate.AcquireAsync("personal", "imap", CancellationToken.None);
        var sameAccount = gate.AcquireAsync("personal", "imap", CancellationToken.None).AsTask();
        var otherAccount = gate.AcquireAsync("work", "imap", CancellationToken.None).AsTask();

        await otherAccount.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(sameAccount.IsCompleted, Is.False);
        });

        await (await otherAccount).DisposeAsync();
        await first.DisposeAsync();
        await (await sameAccount).DisposeAsync();
    }

    [Test]
    public async Task CancelledKeyedAcquisitionReleasesItsGlobalPermitExactlyOnce()
    {
        var gate = new ConnectionGate(new ConnectionLimits(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 3));
        var first = await gate.AcquireAsync("personal", "imap", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var cancelled = gate.AcquireAsync("personal", "imap", cancellation.Token).AsTask();
        cancellation.Cancel();
        Assert.That(async () => await cancelled, Throws.TypeOf<OperationCanceledException>());

        var work = await gate.AcquireAsync("work", "imap", CancellationToken.None);
        var third = await gate.AcquireAsync("third", "imap", CancellationToken.None);
        await work.DisposeAsync();
        await third.DisposeAsync();
        await first.DisposeAsync();
        await first.DisposeAsync();
    }

    [Test]
    public void CommandTimeoutTokenCancelsWithoutCancellingCaller()
    {
        using var scope = CommandTimeoutScope.Create(TimeSpan.FromMilliseconds(25), CancellationToken.None);

        Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await Task.Delay(Timeout.InfiniteTimeSpan, scope.Token));
        Assert.That(scope.IsTimeoutCancellation, Is.True);
    }

    [Test]
    public void CallerCancellationIsNotReportedAsTimeout()
    {
        using var caller = new CancellationTokenSource();
        using var scope = CommandTimeoutScope.Create(TimeSpan.FromMinutes(1), caller.Token);

        caller.Cancel();

        Assert.Multiple(() =>
        {
            Assert.That(scope.Token.IsCancellationRequested, Is.True);
            Assert.That(scope.IsTimeoutCancellation, Is.False);
        });
    }

    [Test]
    public void RejectsNonPositiveCommandTimeout()
    {
        Assert.That(() => CommandTimeoutScope.Create(TimeSpan.Zero, CancellationToken.None),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task ConnectorUsesConfiguredEndpointAndAuthenticatesOnce()
    {
        var proxy = MailServiceProxy.Create();
        var connector = new MailServiceConnector(ConnectionLimits.Default, _ => proxy.Service);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        var service = await connector.ConnectAndAuthenticateAsync(
            "imap", new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
            "user@example.test", credential, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(service, Is.SameAs(proxy.Service));
            Assert.That(proxy.ConnectCalls, Is.EqualTo(1));
            Assert.That(proxy.ConnectHost, Is.EqualTo("imap.example.test"));
            Assert.That(proxy.ConnectPort, Is.EqualTo(993));
            Assert.That(proxy.ConnectOptions, Is.EqualTo(SecureSocketOptions.SslOnConnect));
            Assert.That(proxy.AuthenticateCalls, Is.EqualTo(1));
            Assert.That(proxy.Username, Is.EqualTo("user@example.test"));
        });
    }

    [Test]
    public void ConnectorDisconnectsAndDisposesWhenAuthenticationFails()
    {
        var proxy = MailServiceProxy.Create();
        proxy.AuthenticationFailure = new MailKit.Security.AuthenticationException("private-server-marker");
        var connector = new MailServiceConnector(ConnectionLimits.Default, _ => proxy.Service);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        Assert.That(async () => await connector.ConnectAndAuthenticateAsync(
                "imap", new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
                "user@example.test", credential, CancellationToken.None),
            Throws.TypeOf<MailOperationException>()
                .With.Property("Error").Property("Code").EqualTo("connection.authentication_failed"));
        Assert.Multiple(() =>
        {
            Assert.That(proxy.AuthenticateCalls, Is.EqualTo(1));
            Assert.That(proxy.DisconnectCalls, Is.EqualTo(1));
            Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void CleanupFailureDoesNotReplaceSanitizedAuthenticationError()
    {
        var proxy = MailServiceProxy.Create();
        proxy.AuthenticationFailure = new MailKit.Security.AuthenticationException("private-server-marker");
        proxy.DisposeFailure = new InvalidOperationException("private-cleanup-marker");
        var connector = new MailServiceConnector(ConnectionLimits.Default, _ => proxy.Service);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
            await connector.ConnectAndAuthenticateAsync(
                "imap", new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
                "user@example.test", credential, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Error.Code, Is.EqualTo("connection.authentication_failed"));
            Assert.That(exception.ToString(), Does.Not.Contain("private-cleanup-marker"));
            Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CleanupTimeoutPreservesAuthenticationErrorAndDisposesService()
    {
        var disconnectRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = MailServiceProxy.Create();
        proxy.AuthenticationFailure = new MailKit.Security.AuthenticationException("private-server-marker");
        proxy.DisconnectOperation = _ => disconnectRelease.Task;
        var connector = new MailServiceConnector(new ConnectionLimits(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(25), 1, 1), _ => proxy.Service);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());
        Task<IMailService> operation = connector.ConnectAndAuthenticateAsync(
            "imap", new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
            "user@example.test", credential, CancellationToken.None);

        try
        {
            var exception = Assert.ThrowsAsync<MailOperationException>(async () =>
                await operation.WaitAsync(TimeSpan.FromMilliseconds(500)));

            Assert.Multiple(() =>
            {
                Assert.That(exception!.Error.Code, Is.EqualTo("connection.authentication_failed"));
                Assert.That(proxy.DisconnectCancellationToken.CanBeCanceled, Is.True);
                Assert.That(proxy.DisconnectCancellationToken.IsCancellationRequested, Is.True);
                Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            disconnectRelease.TrySetResult();
            try
            {
                await operation;
            }
            catch
            {
                // The asserted primary authentication error is expected.
            }
        }
    }

    [Test]
    public async Task CallerCancellationAlsoBoundsFailedConnectionCleanup()
    {
        var disconnectRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var proxy = MailServiceProxy.Create();
        proxy.AuthenticationFailure = new OperationCanceledException(caller.Token);
        proxy.DisconnectOperation = _ => disconnectRelease.Task;
        var connector = new MailServiceConnector(ConnectionLimits.Default, _ => proxy.Service);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());
        Task<IMailService> operation = connector.ConnectAndAuthenticateAsync(
            "imap", new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
            "user@example.test", credential, caller.Token);

        try
        {
            Assert.That(async () => await operation.WaitAsync(TimeSpan.FromMilliseconds(500)),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.Multiple(() =>
            {
                Assert.That(proxy.DisconnectCancellationToken.IsCancellationRequested, Is.True);
                Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
            });
        }
        finally
        {
            disconnectRelease.TrySetResult();
            try
            {
                await operation;
            }
            catch
            {
                // Caller cancellation is expected.
            }
        }
    }

    [Test]
    public async Task LateDisconnectFaultRemainsOwnedAndObservedAfterBoundedCleanup()
    {
        var disconnectRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? observedLateFailure = null;
        var cleanupOwner = new FailedServiceCleanupOwner(exception =>
            observedLateFailure = exception);
        var proxy = MailServiceProxy.Create();
        var activeCleanupsWhenDisposed = -1;
        proxy.AuthenticationFailure = new MailKit.Security.AuthenticationException("private-server-marker");
        proxy.DisconnectOperation = _ => disconnectRelease.Task;
        proxy.DisposeOperation = () =>
            activeCleanupsWhenDisposed = cleanupOwner.ActiveCleanupCount;
        var connector = new MailServiceConnector(new ConnectionLimits(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(25), 1, 1), _ => proxy.Service, cleanupOwner);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());
        Task<IMailService> operation = connector.ConnectAndAuthenticateAsync(
            "imap", new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
            "user@example.test", credential, CancellationToken.None);

        MailOperationException? primaryException = null;
        try
        {
            primaryException = Assert.ThrowsAsync<MailOperationException>(async () =>
                await operation.WaitAsync(TimeSpan.FromMilliseconds(500)));

            Assert.Multiple(() =>
            {
                Assert.That(primaryException!.Error.Code, Is.EqualTo("connection.authentication_failed"));
                Assert.That(proxy.DisposeCalls, Is.EqualTo(1));
                Assert.That(activeCleanupsWhenDisposed, Is.EqualTo(1));
                Assert.That(cleanupOwner.ActiveCleanupCount, Is.EqualTo(1));
                Assert.That(observedLateFailure, Is.Null);
            });

            var lateFailure = new InvalidOperationException("private-late-disconnect-marker");
            disconnectRelease.SetException(lateFailure);
            await cleanupOwner.WhenIdleAsync().WaitAsync(TimeSpan.FromMilliseconds(500));

            Assert.Multiple(() =>
            {
                Assert.That(cleanupOwner.ActiveCleanupCount, Is.Zero);
                Assert.That(observedLateFailure, Is.SameAs(lateFailure));
                Assert.That(primaryException!.Error.Code, Is.EqualTo("connection.authentication_failed"));
                Assert.That(primaryException.ToString(), Does.Not.Contain("private-late-disconnect-marker"));
            });
        }
        finally
        {
            disconnectRelease.TrySetResult();
            await cleanupOwner.WhenIdleAsync().WaitAsync(TimeSpan.FromMilliseconds(500));
        }
    }

    [Test]
    public void ConnectorRejectsUnknownProtocolWithStableError()
    {
        var connector = new MailServiceConnector();
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        Assert.That(async () => await connector.ConnectAndAuthenticateAsync(
                "unknown", new EndpointSettings("mail.example.test", 993, TlsMode.ImplicitTls),
                "user@example.test", credential, CancellationToken.None),
            Throws.TypeOf<MailOperationException>()
                .With.Property("Error").Property("Code").EqualTo("connection.protocol_error"));
    }

    [TestCase(TlsMode.Plain)]
    [TestCase((TlsMode)999)]
    public void ConnectorRejectsInsecureTlsBeforeConstructingService(TlsMode tlsMode)
    {
        var factoryCalls = 0;
        var proxy = MailServiceProxy.Create();
        var connector = new MailServiceConnector(ConnectionLimits.Default, _ =>
        {
            factoryCalls++;
            return proxy.Service;
        });
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        Assert.That(async () => await connector.ConnectAndAuthenticateAsync(
                "imap", new EndpointSettings("imap.example.test", 993, tlsMode),
                "user@example.test", credential, CancellationToken.None),
            Throws.TypeOf<MailOperationException>()
                .With.Property("Error").Property("Code").EqualTo("connection.tls_required"));
        Assert.That(factoryCalls, Is.Zero);
    }

    [Test]
    public async Task GateDisposalKeepsActiveAcquisitionsAndLeaseCleanupSafe()
    {
        var gate = new ConnectionGate(new ConnectionLimits(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1));
        var first = await gate.AcquireAsync("personal", "imap", CancellationToken.None);
        var waiting = gate.AcquireAsync("work", "imap", CancellationToken.None).AsTask();

        gate.Dispose();

        Assert.That(async () => await first.DisposeAsync(), Throws.Nothing);
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(async () => await second.DisposeAsync(), Throws.Nothing);
        Assert.That(async () => await first.DisposeAsync(), Throws.Nothing);
        Assert.That(async () => await gate.AcquireAsync("third", "imap", CancellationToken.None),
            Throws.TypeOf<ObjectDisposedException>());
    }

    private class MailServiceProxy : DispatchProxy
    {
        public IMailService Service { get; private set; } = null!;
        public int ConnectCalls { get; private set; }
        public int AuthenticateCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public string? ConnectHost { get; private set; }
        public int ConnectPort { get; private set; }
        public SecureSocketOptions ConnectOptions { get; private set; }
        public string? Username { get; private set; }
        public Exception? AuthenticationFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public Action? DisposeOperation { get; set; }
        public Func<CancellationToken, Task>? DisconnectOperation { get; set; }
        public CancellationToken DisconnectCancellationToken { get; private set; }

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
                case nameof(IMailService.ConnectAsync) when args is [string host, int port, SecureSocketOptions options, CancellationToken]:
                    ConnectCalls++;
                    ConnectHost = host;
                    ConnectPort = port;
                    ConnectOptions = options;
                    return Task.CompletedTask;
                case nameof(IMailService.AuthenticateAsync) when args is [NetworkCredential credential, CancellationToken]:
                    AuthenticateCalls++;
                    Username = credential.UserName;
                    return AuthenticationFailure is null
                        ? Task.CompletedTask
                        : Task.FromException(AuthenticationFailure);
                case nameof(IMailService.DisconnectAsync) when args is [bool, CancellationToken cancellationToken]:
                    DisconnectCalls++;
                    DisconnectCancellationToken = cancellationToken;
                    return DisconnectOperation?.Invoke(cancellationToken) ?? Task.CompletedTask;
                case nameof(IDisposable.Dispose):
                    DisposeCalls++;
                    DisposeOperation?.Invoke();
                    if (DisposeFailure is not null)
                        throw DisposeFailure;
                    return null;
                case "get_IsConnected":
                    return ConnectCalls > DisconnectCalls;
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
