using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using MailKit.Agent.Core.Errors;
using MailKit.Security;
using SystemAuthenticationException = System.Security.Authentication.AuthenticationException;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;

namespace MailKit.Agent.Mail.Connections;

public static class ProtocolExceptionMapper
{
    public static MailOperationException Map(
        Exception exception,
        string protocol,
        string operation,
        CancellationToken callerCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (exception is OperationCanceledException && callerCancellationToken.IsCancellationRequested)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        var details = new Dictionary<string, string>
        {
            ["protocol"] = protocol,
            ["operation"] = operation
        };

        var error = exception switch
        {
            SslHandshakeException => Create(
                "connection.tls_failed", ErrorCategory.Authentication,
                "TLS negotiation failed.", false, details),
            SystemAuthenticationException or MailKitAuthenticationException => Create(
                "connection.authentication_failed", ErrorCategory.Authentication,
                "Authentication failed.", false, details),
            OperationCanceledException => Create(
                "connection.timeout", ErrorCategory.Transient,
                "The mail server operation timed out.", true, details),
            ServiceNotConnectedException => Create(
                "connection.disconnected", ErrorCategory.Transient,
                "The mail server connection was lost.", true, details),
            CommandException => Create(
                "connection.protocol_error", ErrorCategory.Capability,
                "The mail server rejected the requested operation.", false, details),
            ProtocolException => Create(
                "connection.protocol_error", ErrorCategory.Transient,
                "The mail server protocol operation failed.", false, details),
            IOException or SocketException => Create(
                "connection.transport_error", ErrorCategory.Transient,
                "The mail server transport failed.", true, details),
            _ => Create(
                "connection.internal", ErrorCategory.Internal,
                "The mail operation failed.", false, details)
        };

        return new MailOperationException(error);
    }

    private static ToolError Create(
        string code,
        ErrorCategory category,
        string message,
        bool retryable,
        IReadOnlyDictionary<string, string> details) =>
        new(code, category, message, retryable, null, details);
}
