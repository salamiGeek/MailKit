using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MailKit.Agent.Auth.Native;
using MailKit.Agent.Core.Credentials;

namespace MailKit.Agent.Auth;

public sealed class WindowsCredentialVault : IAccountCredentialVault
{
	private const int MaximumCredentialBlobSize = 2560;

	public ValueTask<CredentialStatus> GetStatusAsync(
		string accountId,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureSupported();
		var target = CredentialTarget.Password(accountId);

		if (CredentialNative.CredReadW(
			target,
			CredentialNative.CRED_TYPE_GENERIC,
			0,
			out var credential))
		{
			CredentialNative.CredFree(credential);
			return ValueTask.FromResult(new CredentialStatus(true, CredentialKind.Password));
		}

		return Marshal.GetLastWin32Error() == CredentialNative.ErrorNotFound
			? ValueTask.FromResult(new CredentialStatus(false, null))
			: ValueTask.FromException<CredentialStatus>(CredentialVaultException.OperationFailed());
	}

	public ValueTask<PasswordCredentialLease> GetPasswordAsync(
		string accountId,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureSupported();
		var target = CredentialTarget.Password(accountId);

		if (!CredentialNative.CredReadW(
			target,
			CredentialNative.CRED_TYPE_GENERIC,
			0,
			out var credentialPointer))
		{
			return Marshal.GetLastWin32Error() == CredentialNative.ErrorNotFound
				? ValueTask.FromException<PasswordCredentialLease>(CredentialVaultException.NotConfigured())
				: ValueTask.FromException<PasswordCredentialLease>(CredentialVaultException.OperationFailed());
		}

		try
		{
			var credential = Marshal.PtrToStructure<CredentialNative.Credential>(credentialPointer);
			if (credential.CredentialBlobSize > MaximumCredentialBlobSize ||
				credential.CredentialBlobSize % sizeof(char) != 0)
			{
				return ValueTask.FromException<PasswordCredentialLease>(
					CredentialVaultException.OperationFailed());
			}

			var bytes = new byte[credential.CredentialBlobSize];
			var characters = Array.Empty<char>();
			try
			{
				Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
				characters = Encoding.Unicode.GetChars(bytes);
				return ValueTask.FromResult(PasswordCredentialLease.FromCharacters(characters));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(bytes);
				CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(characters.AsSpan()));
			}
		}
		finally
		{
			CredentialNative.CredFree(credentialPointer);
		}
	}

	public ValueTask SetPasswordAsync(
		string accountId,
		string username,
		ReadOnlyMemory<char> password,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureSupported();
		var target = CredentialTarget.Password(accountId);
		ArgumentNullException.ThrowIfNull(username);

		var byteCount = Encoding.Unicode.GetByteCount(password.Span);
		if (byteCount > MaximumCredentialBlobSize)
			throw new ArgumentException("Password exceeds the credential storage limit.", nameof(password));

		var bytes = new byte[byteCount];
		Encoding.Unicode.GetBytes(password.Span, bytes);
		var handle = default(GCHandle);
		try
		{
			handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
			var credential = new CredentialNative.Credential
			{
				Type = CredentialNative.CRED_TYPE_GENERIC,
				TargetName = target,
				CredentialBlobSize = checked((uint)bytes.Length),
				CredentialBlob = handle.AddrOfPinnedObject(),
				Persist = CredentialNative.CRED_PERSIST_LOCAL_MACHINE,
				UserName = username
			};

			if (!CredentialNative.CredWriteW(ref credential, 0))
				throw CredentialVaultException.OperationFailed();
		}
		finally
		{
			if (handle.IsAllocated)
				handle.Free();
			CryptographicOperations.ZeroMemory(bytes);
		}

		return ValueTask.CompletedTask;
	}

	public ValueTask<bool> DeletePasswordAsync(
		string accountId,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureSupported();
		var target = CredentialTarget.Password(accountId);

		if (CredentialNative.CredDeleteW(target, CredentialNative.CRED_TYPE_GENERIC, 0))
			return ValueTask.FromResult(true);

		return Marshal.GetLastWin32Error() == CredentialNative.ErrorNotFound
			? ValueTask.FromResult(false)
			: ValueTask.FromException<bool>(CredentialVaultException.OperationFailed());
	}

	private static void EnsureSupported()
	{
		if (!OperatingSystem.IsWindows())
			throw CredentialVaultException.PlatformUnsupported();
	}
}
