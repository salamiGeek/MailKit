using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Errors;
using MailKit.Security;

namespace MailKit.Agent.Mail.Connections;

public static class SecureSocketOptionsMapper
{
    public static SecureSocketOptions Map(TlsMode mode) => mode switch
    {
        TlsMode.ImplicitTls => SecureSocketOptions.SslOnConnect,
        TlsMode.StartTls => SecureSocketOptions.StartTls,
        _ => throw new MailOperationException(new ToolError(
            "connection.tls_required",
            ErrorCategory.Validation,
            "A secure TLS mode is required.",
            false,
            null,
            null))
    };
}
