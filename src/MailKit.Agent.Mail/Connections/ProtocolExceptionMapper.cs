using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using MailKit.Agent.Core.Errors;
using MailKit.Security;
using SystemAuthenticationException = System.Security.Authentication.AuthenticationException;
using MailKitAuthenticationException = MailKit.Security.AuthenticationException;

namespace MailKit.Agent.Mail.Connections;

internal static class ProtocolExceptionMapper
{
    internal static MailOperationException Map(
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
            ["protocol"] = SanitizeProtocol(protocol),
            ["operation"] = SanitizeOperation(operation)
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

    private static string SanitizeProtocol(string protocol) => protocol switch
    {
        "imap" => "imap",
        "pop3" => "pop3",
        "smtp" => "smtp",
        _ => "unknown"
    };

    private static string SanitizeOperation(string operation) => operation switch
    {
        "connect" => "connect",
        "authenticate" => "authenticate",
        "connection_test" => "connection_test",
        "folder_list" => "folder_list",
        "message_list" => "message_list",
        "message_search" => "message_search",
        "message_read" => "message_read",
        "message_mark_read" => "message_mark_read",
        "pop3_message_list" => "pop3_message_list",
        "pop3_message_read" => "pop3_message_read",
        "attachment_list" => "attachment_list",
        "attachment_save" => "attachment_save",
        "send_prepare" => "send_prepare",
        "send_commit" => "send_commit",
        "send_status" => "send_status",
        _ => "unknown"
    };

    private static ToolError Create(
        string code,
        ErrorCategory category,
        string message,
        bool retryable,
        IReadOnlyDictionary<string, string> details) =>
        new(code, category, message, retryable, null, details);
}
