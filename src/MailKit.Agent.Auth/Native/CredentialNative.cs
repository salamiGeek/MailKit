using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MailKit.Agent.Auth.Native;

internal static class CredentialNative
{
	internal const uint CRED_TYPE_GENERIC = 1;
	internal const uint CRED_PERSIST_LOCAL_MACHINE = 2;
	internal const int ErrorNotFound = 1168;

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	internal struct Credential
	{
		public uint Flags;
		public uint Type;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string TargetName;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string? Comment;
		public FILETIME LastWritten;
		public uint CredentialBlobSize;
		public IntPtr CredentialBlob;
		public uint Persist;
		public uint AttributeCount;
		public IntPtr Attributes;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string? TargetAlias;
		[MarshalAs(UnmanagedType.LPWStr)]
		public string UserName;
	}

	[DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CredReadW(
		string target,
		uint type,
		uint flags,
		out IntPtr credential);

	[DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CredWriteW(ref Credential credential, uint flags);

	[DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static extern bool CredDeleteW(string target, uint type, uint flags);

	[DllImport("Advapi32.dll", EntryPoint = "CredFree")]
	internal static extern void CredFree(IntPtr buffer);
}
