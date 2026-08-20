using System.Globalization;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Sending;

namespace MailKit.Agent.Mail.Tests.Sending;

/// <summary>
/// Unit tests for the pure dialog-text builder of the Windows local approval
/// gate. The MessageBox itself is intentionally never opened here: it blocks
/// until a human clicks, which would hang CI (see the note on
/// <see cref="ApproveWithCanceledTokenReturnsFalseWithoutShowingADialog"/> for
/// the cancellation plumbing that IS covered).
/// </summary>
public class WindowsSendCommitApproverTests
{
	private const string DialogTitle = "MailKit Agent send confirmation";

	[Test]
	public void BuildApprovalTextRendersEveryPreviewField()
	{
		SendPreview preview = Preview();

		string text = WindowsSendCommitApprover.BuildApprovalText(preview);

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Contain("From: Bob <user@example.com>"));
			Assert.That(text, Does.Contain("To: Alice <alice@example.com>"));
			Assert.That(text, Does.Contain("Cc: carol@example.com"));
			Assert.That(text, Does.Contain("Bcc: hidden@example.com"));
			Assert.That(text, Does.Contain("Subject: Fixture subject"));
			Assert.That(text, Does.Contain("Body preview (first 200 characters):"));
			Assert.That(text, Does.Contain("fixture body preview"));
			Assert.That(text, Does.Contain("Attachments (2): report.txt, data.zip"));
			Assert.That(text, Does.Contain("Message-Id: <fixture-message-id@example.test>"));
			Assert.That(text, Does.Contain("Idempotency key hash: " + preview.IdempotencyKeyHash));
			Assert.That(
				text,
				Does.Contain("Expires (local time): " + LocalExpiry(preview)),
				"The expiry must be rendered in the machine's local time.");
			Assert.That(
				text,
				Does.Contain("Bcc recipients are shown in this local dialog only"),
				"The dialog must clearly mark that Bcc recipients appear locally only.");
			Assert.That(
				text,
				Does.Contain("Approve this send?"),
				"The dialog must end with an explicit approval question.");
		});
	}

	[Test]
	public void BuildApprovalTextNeverContainsTheConfirmationTokenOrEmptyFieldNoise()
	{
		SendPreview preview = Preview() with { ConfirmationToken = "hmac-secret-confirmation" };

		string text = WindowsSendCommitApprover.BuildApprovalText(preview);

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Not.Contain("hmac-secret-confirmation"),
				"The dialog must never display the confirmation token.");
			Assert.That(text, Does.Not.Contain("confirmation_token"));
		});
	}

	[Test]
	public void BuildApprovalTextRendersNoneForMissingOptionalFields()
	{
		SendPreview preview = Preview() with
		{
			From = null,
			Cc = [],
			Bcc = [],
			Subject = null,
			TextPreview = null,
			AttachmentCount = 0,
			AttachmentNames = []
		};

		string text = WindowsSendCommitApprover.BuildApprovalText(preview);

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Contain("From: (none)"));
			Assert.That(text, Does.Contain("Cc: (none)"));
			Assert.That(text, Does.Contain("Bcc: (none)"));
			Assert.That(text, Does.Contain("Subject: (none)"));
			Assert.That(text, Does.Contain("(no text body)"));
			Assert.That(text, Does.Contain("Attachments: none"));
		});
	}

	[Test]
	public void BuildApprovalTextTruncatesBodyPreviewToTwoHundredCharacters()
	{
		SendPreview preview = Preview() with { TextPreview = new string('x', 250) };

		string text = WindowsSendCommitApprover.BuildApprovalText(preview);

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Contain(new string('x', SendApplication.TextPreviewLength)));
			Assert.That(text, Does.Not.Contain(new string('x', SendApplication.TextPreviewLength + 1)),
				"The dialog must show at most the 200 characters the preview carries.");
		});
	}

	[Test]
	public void BuildApprovalTextKeepsHostileUntrustedContentInert()
	{
		SendPreview preview = Preview() with
		{
			Subject = "Invoice\r\nCc: attacker@example.test\r\nWARNING: already approved",
			To = new[] { "Alice <alice@example.com>\nBcc: sneaky@example.test>" }
		};

		string text = WindowsSendCommitApprover.BuildApprovalText(preview);

		Assert.Multiple(() =>
		{
			Assert.That(text, Does.Not.Contain("Cc: attacker@example.test\r\n"));
			foreach (string line in text.Split('\n'))
			{
				Assert.That(line.Trim(), Does.Not.StartWith("Cc: attacker"),
					"Hostile content must not forge dialog lines.");
				Assert.That(line.Trim(), Does.Not.StartWith("Bcc: sneaky"),
					"Hostile content must not forge dialog lines.");
			}

			Assert.That(text, Does.Contain("Subject: Invoice"),
				"The hostile subject stays visible, flattened onto its own line.");
		});
	}

	[Test]
	public void DialogTitleIsEnglishAndStable()
	{
		// The dialog caption is matched by the cancellation close plumbing, so it
		// must stay stable and ASCII (codebase language is English).
		Assert.That(DialogTitle, Is.EqualTo("MailKit Agent send confirmation"));
		Assert.That(WindowsSendCommitApprover.DialogTitle, Is.EqualTo(DialogTitle));
	}

	[Test]
	public async Task ApproveWithCanceledTokenDeclinesWithoutShowingADialog()
	{
		// Exercises the cancellation entry guard of the real approver without ever
		// opening a visible MessageBox (an already-canceled token must decline
		// synchronously, which keeps CI non-interactive). Two branches are
		// deliberately untested because observing them headlessly is impossible:
		// the input-desktop probe on a machine WITH an interactive desktop (it
		// would proceed to a real, human-clickable dialog) and the WM_CLOSE
		// mid-wait dismissal path.
		if (!OperatingSystem.IsWindows())
			Assert.Ignore("The Windows approver's dialog plumbing only runs on Windows.");

		var approver = new WindowsSendCommitApprover();
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();

		SendApprovalOutcome outcome = await approver.ApproveAsync(Preview(), canceled.Token);

		Assert.That(outcome, Is.EqualTo(SendApprovalOutcome.Declined),
			"A canceled approval wait must decline without showing any dialog.");
	}

	internal static string LocalExpiry(SendPreview preview) =>
		preview.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

	private static SendPreview Preview() => new(
		"prep-1",
		"personal",
		"<fixture-message-id@example.test>",
		"Bob <user@example.com>",
		["Alice <alice@example.com>"],
		["carol@example.com"],
		["hidden@example.com"],
		"Fixture subject",
		"fixture body preview",
		2,
		["report.txt", "data.zip"],
		new DateTimeOffset(2026, 8, 19, 4, 0, 0, TimeSpan.Zero),
		new DateTimeOffset(2026, 8, 19, 4, 10, 0, TimeSpan.Zero),
		new string('a', 64),
		new string('b', 64),
		"fixture-confirmation-token");
}
