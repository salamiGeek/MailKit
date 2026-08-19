using System.Net;
using System.Reflection;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Connections;
using MailKit.Agent.Mail.Imap;
using MailKit.Agent.Mail.Pop3;
using MailKit.Agent.Mail.Smtp;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
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

    [Test]
    public async Task ImapGatewayHoldsGateLeasesFromConnectThroughDisconnect()
    {
        var gate = new ConnectionGate(LimitsForTwoConcurrentConnections());
        var factory = new SlowImapClientFactory();
        var gateway = new ImapGateway(
            factory, commandTimeout: TimeSpan.FromSeconds(5), connectionGate: gate);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        Task[] operations = Enumerable.Range(0, 5)
            .Select(_ => gateway.ListFoldersAsync(Profile(), credential, CancellationToken.None))
            .ToArray();

        await factory.WaitUntilEnteredAsync(2);
        await Task.Delay(150);
        Assert.That(factory.Entered, Is.EqualTo(2),
            "Only MaxPerAccountProtocol IMAP connections may proceed concurrently.");

        factory.ReleaseConnects();
        Exception?[] failures = await Task.WhenAll(operations.Select(CaptureFailureAsync));

        Assert.Multiple(() =>
        {
            Assert.That(factory.Entered, Is.EqualTo(5),
                "Queued operations must proceed once a lease is released, never fail.");
            Assert.That(factory.MaxConcurrent, Is.EqualTo(2));
            Assert.That(failures, Has.All.InstanceOf<MailOperationException>());
        });
    }

    [Test]
    public async Task Pop3GatewayHoldsGateLeasesFromConnectThroughDisconnect()
    {
        var gate = new ConnectionGate(LimitsForTwoConcurrentConnections());
        var factory = new SlowPop3ClientFactory();
        var gateway = new Pop3Gateway(
            factory, commandTimeout: TimeSpan.FromSeconds(5), connectionGate: gate);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        Task[] operations = Enumerable.Range(0, 5)
            .Select(_ => gateway.ListMessagesAsync(
                Profile(), credential, 0, 1, CancellationToken.None))
            .ToArray();

        await factory.WaitUntilEnteredAsync(2);
        await Task.Delay(150);
        Assert.That(factory.Entered, Is.EqualTo(2),
            "Only MaxPerAccountProtocol POP3 connections may proceed concurrently.");

        factory.ReleaseConnects();
        Exception?[] failures = await Task.WhenAll(operations.Select(CaptureFailureAsync));

        Assert.Multiple(() =>
        {
            Assert.That(factory.Entered, Is.EqualTo(5));
            Assert.That(factory.MaxConcurrent, Is.EqualTo(2));
            Assert.That(failures, Has.All.InstanceOf<MailOperationException>());
        });
    }

    [Test]
    public async Task SmtpGatewayHoldsGateLeasesFromConnectThroughDisconnect()
    {
        var gate = new ConnectionGate(LimitsForTwoConcurrentConnections());
        var factory = new SlowSmtpClientFactory();
        var gateway = new SmtpGateway(
            factory, commandTimeout: TimeSpan.FromSeconds(5), connectionGate: gate);
        using var credential = PasswordCredentialLease.FromCharacters("test-password".AsSpan());

        Task<SendTransportOutcome>[] operations = Enumerable.Range(0, 5)
            .Select(_ => gateway.SendAsync(
                Profile(), credential, PreparedMessage(), CancellationToken.None))
            .ToArray();

        await factory.WaitUntilEnteredAsync(2);
        await Task.Delay(150);
        Assert.That(factory.Entered, Is.EqualTo(2),
            "Only MaxPerAccountProtocol SMTP connections may proceed concurrently.");

        factory.ReleaseConnects();
        SendTransportOutcome[] outcomes = await Task.WhenAll(operations);

        Assert.Multiple(() =>
        {
            Assert.That(factory.Entered, Is.EqualTo(5));
            Assert.That(factory.MaxConcurrent, Is.EqualTo(2));
            Assert.That(outcomes.Select(outcome => outcome.State),
                Has.All.EqualTo(SendState.Failed));
        });
    }

    private static ConnectionLimits LimitsForTwoConcurrentConnections() => new(
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5), 2, 8);

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.test", AuthenticationKind.Password,
        new EndpointSettings("imap.example.test", 993, TlsMode.ImplicitTls),
        null,
        new EndpointSettings("smtp.example.test", 465, TlsMode.ImplicitTls));

    private static PreparedOutgoingMessage PreparedMessage()
    {
        const string mime =
            "From: user@example.test\r\n" +
            "To: alice@example.test\r\n" +
            "Subject: Gate wiring\r\n" +
            "\r\n" +
            "gate wiring body\r\n";
        return new PreparedOutgoingMessage(
            "prep-1",
            "personal",
            "<id-1@mailkit-agent.local>",
            new string('a', 64),
            System.Text.Encoding.UTF8.GetBytes(mime),
            "user@example.test",
            ["alice@example.test"],
            new SendPreview(
                "prep-1", "personal", "<id-1@mailkit-agent.local>", null,
                [], [], [], "Gate wiring", null, 0, [],
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10),
                new string('c', 64), new string('d', 64), string.Empty),
            new string('b', 64),
            DateTimeOffset.UtcNow.AddMinutes(10));
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private abstract class SlowClientFactoryBase
    {
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int totalEntered;
        private int occupancy;
        private int maxConcurrent;

        public int Entered => Volatile.Read(ref totalEntered);

        public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

        public void ReleaseConnects() => release.TrySetResult();

        public async Task WaitUntilEnteredAsync(int expected)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Entered < expected)
            {
                Assert.That(cancellation.IsCancellationRequested, Is.False,
                    $"Only {Entered} of {expected} expected connects started.");
                await Task.Delay(10, cancellation.Token);
            }
        }

        protected async Task<T> BlockUntilReleasedAsync<T>()
        {
            Interlocked.Increment(ref totalEntered);
            int current = Interlocked.Increment(ref occupancy);
            int observed;
            while ((observed = Volatile.Read(ref maxConcurrent)) < current)
                Interlocked.CompareExchange(ref maxConcurrent, current, observed);

            try
            {
                await release.Task.ConfigureAwait(false);
                throw new InvalidOperationException("fixture-connect-stop");
            }
            finally
            {
                Interlocked.Decrement(ref occupancy);
            }
        }
    }

    private sealed class SlowImapClientFactory : SlowClientFactoryBase, IImapClientFactory
    {
        public Task<ImapClient> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            BlockUntilReleasedAsync<ImapClient>();
    }

    private sealed class SlowPop3ClientFactory : SlowClientFactoryBase, IPop3ClientFactory
    {
        public Task<Pop3Client> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            BlockUntilReleasedAsync<Pop3Client>();
    }

    private sealed class SlowSmtpClientFactory : SlowClientFactoryBase, ISmtpClientFactory
    {
        public Task<SmtpClient> CreateAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            CancellationToken cancellationToken) =>
            BlockUntilReleasedAsync<SmtpClient>();
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
