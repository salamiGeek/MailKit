using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MailKit.Agent.Core.Sending;

namespace MailKit.Agent.Mail.Sending;

/// <summary>
/// Windows implementation of the local human-approval gate for send commits.
/// <see cref="ApproveAsync"/> shows a topmost Yes/No <c>MessageBoxW</c> that renders
/// the exact redacted preview the caller prepared; only an explicit local Yes
/// approves the delivery. The dialog contains no secrets: <see cref="SendPreview"/>
/// is secret-free by construction and the confirmation token it carries is never
/// rendered.
///
/// Threat model (honest): the dialog blocks casual or automated commit chaining —
/// an MCP caller cannot chain prepare+commit in one unattended run because a human
/// must physically approve the box. A determined same-user automation could
/// theoretically drive the UI from the same session; this is a hard operational
/// gate, not a cryptographic boundary against the machine owner.
/// </summary>
public sealed class WindowsSendCommitApprover : ISendCommitApprover
{
	internal const string DialogTitle = "MailKit Agent send confirmation";

	private const int IdYes = 6;
	private const uint MessageBoxTopmost = 0x00040000;
	private const uint MessageBoxYesNo = 0x00000004;
	private const uint MessageBoxIconWarning = 0x00000030;
	private const uint MessageBoxFlags = MessageBoxTopmost | MessageBoxYesNo | MessageBoxIconWarning;
	private const uint WmClose = 0x0010;

	/// <inheritdoc />
	public async ValueTask<bool> ApproveAsync(
		SendPreview preview, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(preview);

		// Defense in depth: production DI never registers this approver off
		// Windows, and an unavailable interactive desktop must decline, not hang.
		if (!OperatingSystem.IsWindows())
			return false;
		if (cancellationToken.IsCancellationRequested)
			return false;

		var dialog = new DialogSession(BuildApprovalText(preview));
		using CancellationTokenRegistration registration =
			cancellationToken.Register(dialog.Cancel);

		// MessageBoxW needs its own UI thread; STA keeps window ownership clean.
		var thread = new Thread(dialog.Run)
		{
			IsBackground = true,
			Name = "MailKit.Agent send approval"
		};
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();

		// The wait is bounded only by the caller's token: cancellation dismisses
		// the box (WM_CLOSE by caption) and resolves as declined.
		return await dialog.Completion.Task.ConfigureAwait(false);
	}

	/// <summary>
	/// Pure, unit-testable rendering of the dialog body. Every preview field is
	/// displayed EXCEPT the confirmation token; untrusted fields (subject,
	/// addresses, display names, attachment names, body preview) are flattened so
	/// hostile content can never forge dialog lines.
	/// </summary>
	internal static string BuildApprovalText(SendPreview preview)
	{
		ArgumentNullException.ThrowIfNull(preview);

		var builder = new StringBuilder(512);
		builder.AppendLine("MailKit Agent is about to send the following message from this computer.");
		builder.AppendLine();
		builder.Append("From: ").AppendLine(Scalar(preview.From));
		builder.Append("To: ").AppendLine(List(preview.To));
		builder.Append("Cc: ").AppendLine(List(preview.Cc));
		builder.Append("Bcc: ").AppendLine(List(preview.Bcc));
		builder.Append("Subject: ").AppendLine(Scalar(preview.Subject));
		builder.AppendLine();
		builder.AppendLine(
			$"Body preview (first {SendApplication.TextPreviewLength} characters):");
		builder.AppendLine(BodyPreview(preview));
		builder.AppendLine();
		builder.AppendLine(Attachments(preview));
		builder.AppendLine($"Message-Id: {Flatten(preview.MessageId)}");
		builder.AppendLine($"Idempotency key hash: {Flatten(preview.IdempotencyKeyHash)}");
		builder.AppendLine(
			$"Expires (local time): {preview.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
		builder.AppendLine();
		builder.AppendLine("Bcc recipients are shown in this local dialog only.");
		builder.AppendLine();
		builder.Append("Approve this send?");

		return builder.ToString();
	}

	private static string Scalar(string? value) =>
		string.IsNullOrWhiteSpace(value) ? "(none)" : Flatten(value);

	private static string List(IReadOnlyList<string>? values) =>
		values is null || values.Count == 0
			? "(none)"
			: string.Join(", ", values.Select(value => string.IsNullOrWhiteSpace(value) ? "(none)" : Flatten(value)));

	private static string BodyPreview(SendPreview preview)
	{
		if (string.IsNullOrWhiteSpace(preview.TextPreview))
			return "(no text body)";

		string flattened = Flatten(preview.TextPreview);
		return flattened.Length <= SendApplication.TextPreviewLength
			? flattened
			: flattened[..SendApplication.TextPreviewLength];
	}

	private static string Attachments(SendPreview preview)
	{
		if (preview.AttachmentCount == 0 || preview.AttachmentNames.Count == 0)
			return "Attachments: none";
		return "Attachments (" + preview.AttachmentCount + "): "
			+ string.Join(", ", preview.AttachmentNames.Select(name => Flatten(name)));
	}

	/// <summary>
	/// Replaces control characters (including CR/LF) with spaces so untrusted data
	/// stays inert and can never inject fake dialog lines.
	/// </summary>
	private static string Flatten(string value)
	{
		if (value.AsSpan().IndexOfAny('\r', '\n', '\t') < 0 &&
		    !value.Any(char.IsControl))
			return value;

		var builder = new StringBuilder(value.Length);
		foreach (char character in value)
			builder.Append(char.IsControl(character) ? ' ' : character);
		return builder.ToString();
	}

	private sealed class DialogSession(string text)
	{
		private readonly string text = text;
		private int dialogThreadId;

		public TaskCompletionSource<bool> Completion { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Run()
		{
			Volatile.Write(ref dialogThreadId, GetCurrentThreadId());
			if (Completion.Task.IsCompleted)
			{
				// Cancellation already resolved the wait before the dialog appeared.
				return;
			}

			int result = MessageBoxW(IntPtr.Zero, this.text, DialogTitle, MessageBoxFlags);
			Completion.TrySetResult(result == IdYes);
		}

		public void Cancel()
		{
			DismissVisibleDialog();
			Completion.TrySetResult(false);
		}

		private void DismissVisibleDialog()
		{
			int threadId = Volatile.Read(ref dialogThreadId);
			if (threadId == 0)
				return;

			EnumThreadWindows(
				(uint)threadId,
				(window, _) =>
				{
					var caption = new StringBuilder(256);
					if (GetWindowTextW(window, caption, caption.Capacity + 1) > 0 &&
					    caption.ToString() == DialogTitle)
						PostMessageW(window, WmClose, IntPtr.Zero, IntPtr.Zero);
					return true;
				},
				IntPtr.Zero);
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW", SetLastError = true)]
	private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

	private delegate bool EnumThreadWindowsProc(IntPtr window, IntPtr parameter);

	[DllImport("user32.dll", EntryPoint = "EnumThreadWindows", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool EnumThreadWindows(
		uint threadId, EnumThreadWindowsProc callback, IntPtr parameter);

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", SetLastError = true)]
	private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximumCount);

	[DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool PostMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", SetLastError = true)]
	private static extern int GetCurrentThreadId();
}
