using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MailKit.Agent.Auth.Native;

internal static class DpapiNative
{
	private const uint CryptProtectUiForbidden = 0x1;

	[StructLayout(LayoutKind.Sequential)]
	internal struct DataBlob
	{
		public int Size;
		public IntPtr Data;
	}

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CryptProtectData(
		ref DataBlob input,
		string? description,
		ref DataBlob entropy,
		IntPtr reserved,
		IntPtr prompt,
		uint flags,
		out DataBlob output);

	[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CryptUnprotectData(
		ref DataBlob input,
		IntPtr description,
		ref DataBlob entropy,
		IntPtr reserved,
		IntPtr prompt,
		uint flags,
		out DataBlob output);

	[DllImport("kernel32.dll", SetLastError = true)]
	internal static extern IntPtr LocalAlloc(uint flags, UIntPtr bytes);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool LocalFree(IntPtr memory);

	internal static byte[] Protect(byte[] plain, byte[] entropy)
	{
		var input = BlobOf(plain);
		var entropyBlob = BlobOf(entropy);
		try
		{
			if (!CryptProtectData(
					ref input,
					"MailKit Agent send confirmation secrets",
					ref entropyBlob,
					IntPtr.Zero,
					IntPtr.Zero,
					CryptProtectUiForbidden,
					out DataBlob output))
				throw new CryptographicException(Marshal.GetLastWin32Error());

			try
			{
				return BytesOf(output);
			}
			finally
			{
				LocalFree(output.Data);
			}
		}
		finally
		{
			FreeBlob(input);
			FreeBlob(entropyBlob);
		}
	}

	internal static byte[] Unprotect(byte[] blob, byte[] entropy)
	{
		var input = BlobOf(blob);
		var entropyBlob = BlobOf(entropy);
		try
		{
			if (!CryptUnprotectData(
					ref input,
					IntPtr.Zero,
					ref entropyBlob,
					IntPtr.Zero,
					IntPtr.Zero,
					CryptProtectUiForbidden,
					out DataBlob output))
				throw new CryptographicException(Marshal.GetLastWin32Error());

			try
			{
				return BytesOf(output);
			}
			finally
			{
				LocalFree(output.Data);
			}
		}
		finally
		{
			FreeBlob(input);
			FreeBlob(entropyBlob);
		}
	}

	private static DataBlob BlobOf(byte[] value)
	{
		IntPtr memory = LocalAlloc(0, (UIntPtr)value.Length);
		if (memory == IntPtr.Zero)
			throw new OutOfMemoryException();

		Marshal.Copy(value, 0, memory, value.Length);
		return new DataBlob { Size = value.Length, Data = memory };
	}

	private static void FreeBlob(DataBlob blob)
	{
		if (blob.Data != IntPtr.Zero)
			LocalFree(blob.Data);
	}

	private static byte[] BytesOf(DataBlob blob)
	{
		var result = new byte[blob.Size];
		Marshal.Copy(blob.Data, result, 0, blob.Size);
		return result;
	}
}
