using System.Net.Sockets;
using System.Text.Json;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Mail.Connections;
using MailKit.Security;

namespace MailKit.Agent.Mail.Tests.Connections;

public sealed class ProtocolExceptionMapperTests
{
    [TestCase("system_auth", "connection.authentication_failed", ErrorCategory.Authentication, false)]
    [TestCase("mailkit_auth", "connection.authentication_failed", ErrorCategory.Authentication, false)]
    [TestCase("tls", "connection.tls_failed", ErrorCategory.Authentication, false)]
    [TestCase("timeout", "connection.timeout", ErrorCategory.Transient, true)]
    [TestCase("disconnected", "connection.disconnected", ErrorCategory.Transient, true)]
    [TestCase("command", "connection.protocol_error", ErrorCategory.Capability, false)]
    [TestCase("protocol", "connection.protocol_error", ErrorCategory.Transient, false)]
    [TestCase("io", "connection.transport_error", ErrorCategory.Transient, true)]
    [TestCase("socket", "connection.transport_error", ErrorCategory.Transient, true)]
    [TestCase("other", "connection.internal", ErrorCategory.Internal, false)]
    public void MapsExceptionsToStableSanitizedErrors(
        string exceptionKind, string code, ErrorCategory category, bool retryable)
    {
        var exception = CreateException(exceptionKind);
        var mapped = ProtocolExceptionMapper.Map(
            exception, "imap", "message_list", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(mapped.Error.Code, Is.EqualTo(code));
            Assert.That(mapped.Error.Category, Is.EqualTo(category));
            Assert.That(mapped.Error.Retryable, Is.EqualTo(retryable));
            Assert.That(mapped.InnerException, Is.Null);
        });

        var serialized = JsonSerializer.Serialize(mapped.Error);
        Assert.Multiple(() =>
        {
            Assert.That(serialized, Does.Not.Contain("private-server-marker"));
            Assert.That(serialized, Does.Not.Contain(exception.GetType().Name));
            Assert.That(mapped.ToString(), Does.Not.Contain("private-server-marker"));
            Assert.That(mapped.ToString(), Does.Not.Contain(exception.GetType().Name));
        });
    }

    [Test]
    public void CallerCancellationPropagatesOriginalCancellation()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var cancellation = new OperationCanceledException("private-server-marker", caller.Token);

        var thrown = Assert.Throws<OperationCanceledException>(() =>
            ProtocolExceptionMapper.Map(cancellation, "imap", "message_list", caller.Token));

        Assert.That(thrown, Is.SameAs(cancellation));
    }

    private static Exception CreateException(string kind) => kind switch
    {
        "system_auth" => new System.Security.Authentication.AuthenticationException("private-server-marker"),
        "mailkit_auth" => new MailKit.Security.AuthenticationException("private-server-marker"),
        "tls" => new SslHandshakeException("private-server-marker"),
        "timeout" => new OperationCanceledException("private-server-marker"),
        "disconnected" => new ServiceNotConnectedException("private-server-marker"),
        "command" => new TestCommandException("private-server-marker"),
        "protocol" => new TestProtocolException("private-server-marker"),
        "io" => new IOException("private-server-marker"),
        "socket" => new SocketException((int)SocketError.ConnectionReset),
        _ => new InvalidOperationException("private-server-marker")
    };

    private sealed class TestCommandException(string message) : CommandException(message);

    private sealed class TestProtocolException(string message) : ProtocolException(message);
}
