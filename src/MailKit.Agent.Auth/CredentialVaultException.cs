namespace MailKit.Agent.Auth;

public sealed class CredentialVaultException : Exception
{
	private CredentialVaultException(string code, string message)
		: base(message)
	{
		Code = code;
	}

	public string Code { get; }

	public static CredentialVaultException NotConfigured() =>
		new("credential.not_configured", "The account credential is not configured.");

	public static CredentialVaultException PlatformUnsupported() =>
		new("credential.platform_unsupported", "Credential storage is not supported on this platform.");

	internal static CredentialVaultException OperationFailed() =>
		new("credential.operation_failed", "The credential operation failed.");
}
