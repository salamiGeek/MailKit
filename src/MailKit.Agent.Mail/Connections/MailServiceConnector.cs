using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;

namespace MailKit.Agent.Mail.Connections;

public sealed class MailServiceConnector
{
    private readonly ConnectionLimits _limits;
    private readonly Func<string, IMailService> _serviceFactory;

    public MailServiceConnector(ConnectionLimits? limits = null)
        : this(limits ?? ConnectionLimits.Default, CreateService)
    {
    }

    internal MailServiceConnector(
        ConnectionLimits limits,
        Func<string, IMailService> serviceFactory)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
    }

    public async Task<IMailService> ConnectAndAuthenticateAsync(
        string protocol,
        EndpointSettings endpoint,
        string username,
        PasswordCredentialLease credential,
        CancellationToken callerCancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protocol);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(credential);

        var normalizedProtocol = NormalizeProtocol(protocol);
        var socketOptions = SecureSocketOptionsMapper.Map(endpoint.Tls);
        var service = _serviceFactory(normalizedProtocol);
        var operation = "connect";
        try
        {
            using (var connectScope = CommandTimeoutScope.Create(
                       _limits.ConnectTimeout, callerCancellationToken))
            {
                await service.ConnectAsync(
                    endpoint.Host,
                    endpoint.Port,
                    socketOptions,
                    connectScope.Token).ConfigureAwait(false);
            }

            operation = "authenticate";
            using (var authenticateScope = CommandTimeoutScope.Create(
                       _limits.AuthenticateTimeout, callerCancellationToken))
            {
                var networkCredential = credential.CreateNetworkCredential(username);
                await service.AuthenticateAsync(
                    networkCredential,
                    authenticateScope.Token).ConfigureAwait(false);
            }

            return service;
        }
        catch (Exception exception)
        {
            await CleanupFailedServiceAsync(
                service, _limits.CommandTimeout, callerCancellationToken).ConfigureAwait(false);
            throw ProtocolExceptionMapper.Map(
                exception, normalizedProtocol, operation, callerCancellationToken);
        }
    }

    private static IMailService CreateService(string protocol)
    {
        var logger = new NullProtocolLogger();
        try
        {
            return protocol.ToLowerInvariant() switch
            {
                "imap" => new ImapClient(logger),
                "pop3" => new Pop3Client(logger),
                "smtp" => new SmtpClient(logger),
                _ => throw new ArgumentException("Unsupported mail protocol.", nameof(protocol))
            };
        }
        catch
        {
            logger.Dispose();
            throw;
        }
    }

    private static string NormalizeProtocol(string protocol)
    {
        var normalized = protocol.ToLowerInvariant();
        if (normalized is "imap" or "pop3" or "smtp")
            return normalized;

        throw new MailOperationException(new ToolError(
            "connection.protocol_error",
            ErrorCategory.Capability,
            "The requested mail protocol is not supported.",
            false,
            null,
            null));
    }

    private static async Task CleanupFailedServiceAsync(
        IMailService service,
        TimeSpan timeout,
        CancellationToken callerCancellationToken)
    {
        try
        {
            using var cleanupScope = CommandTimeoutScope.Create(
                timeout, callerCancellationToken);
            Task disconnect = service.DisconnectAsync(false, cleanupScope.Token);
            await disconnect.WaitAsync(cleanupScope.Token).ConfigureAwait(false);
        }
        catch
        {
            // Failure cleanup must not replace the stable operation error.
        }
        finally
        {
            try
            {
                service.Dispose();
            }
            catch
            {
                // Failure cleanup must not replace the stable operation error.
            }
        }
    }
}
