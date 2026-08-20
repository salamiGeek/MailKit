using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using MailKit.Agent.Core.Sending;

namespace MailKit.Agent.Mail.Sending;

/// <summary>
/// Windows implementation of the local human-approval gate for send commits.
/// <see cref="ApproveAsync"/> first honors an already-cancelled token (the
/// outcome is <see cref="SendApprovalOutcome.Declined"/> with no probe and no
/// dialog), then PROBES the interactive input desktop
/// (<c>OpenInputDesktop</c>; the handle is closed immediately): if the process has
/// no input desktop — headless service session, disconnected session — the outcome
/// is <see cref="SendApprovalOutcome.Unavailable"/> and NO dialog is attempted, so
/// the caller fails fast with <c>send.approval_unavailable</c>. With an input
/// desktop present, a topmost Yes/No <c>MessageBoxW</c> renders the exact redacted
/// preview the caller prepared; the dialog pends until the operator answers or the
/// caller's cancellation token fires (the box is dismissed via WM_CLOSE and the
/// outcome resolves as declined). Only an explicit local Yes maps to
/// <see cref="SendApprovalOutcome.Approved"/>. The dialog contains no secrets:
/// <see cref="SendPreview"/> is secret-free by construction and the confirmation
/// token it carries is never rendered.
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
	private const uint DesktopReadobjects = 0x0001;

	/// <inheritdoc />
	public async ValueTask<SendApprovalOutcome> ApproveAsync(
		SendPreview preview, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(preview);

		// Production DI never registers this approver off Windows, and without
		// Windows there is no way to ask a human locally.
		if (!OperatingSystem.IsWindows())
			return SendApprovalOutcome.Unavailable;

		// A caller that already cancelled its approval wait declines locally —
		// BEFORE the desktop probe, so the cancelled→declined mapping (the same one
		// WM_CLOSE dismissal uses) is deterministic on every Windows host, headless
		// ones included, and no probe or dialog runs at all.
		if (cancellationToken.IsCancellationRequested)
			return SendApprovalOutcome.Declined;

		// Probe-first fail fast: without an interactive input desktop nobody could
		// ever see or answer the dialog, so report the environment as unavailable
		// instead of attempting (or hanging on) a MessageBox.
		if (!HasInputDesktop())
			return SendApprovalOutcome.Unavailable;

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

		// With an input desktop present the wait is bounded only by the caller's
		// token: cancellation dismisses the box (WM_CLOSE by caption) and resolves
		// the outcome as declined.
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

	/// <summary>
	/// Returns true when this process has an interactive input desktop. The probe
	/// opens the input desktop with read access and closes the handle immediately:
	/// failure means the process runs on a non-interactive window station (for
	/// example a headless service session) where nobody could answer a dialog.
	/// </summary>
	private static bool HasInputDesktop()
	{
		IntPtr desktop = OpenInputDesktop(0, inheritHandle: false, DesktopReadobjects);
		if (desktop == IntPtr.Zero)
			return false;

		CloseDesktop(desktop);
		return true;
	}

	private sealed class DialogSession(string text)
	{
		private readonly string text = text;
		private int dialogThreadId;

		public TaskCompletionSource<SendApprovalOutcome> Completion { get; } =
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
			Completion.TrySetResult(result == IdYes
				? SendApprovalOutcome.Approved
				: SendApprovalOutcome.Declined);
		}

		public void Cancel()
		{
			DismissVisibleDialog();
			Completion.TrySetResult(SendApprovalOutcome.Declined);
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

	[DllImport("user32.dll", EntryPoint = "OpenInputDesktop", SetLastError = true)]
	private static extern IntPtr OpenInputDesktop(
		uint flags, bool inheritHandle, uint desiredAccess);

	[DllImport("user32.dll", EntryPoint = "CloseDesktop", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseDesktop(IntPtr desktop);
}
