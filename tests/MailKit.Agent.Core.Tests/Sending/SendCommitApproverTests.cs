using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Sending;

namespace MailKit.Agent.Core.Tests.Sending;

/// <summary>
/// Contract tests for the Core-provided send-commit approvers. The Windows
/// dialog approver lives in MailKit.Agent.Mail and its interactive branch is
/// covered structurally only (a real MessageBox would block CI); see the Mail
/// test suite.
/// </summary>
public class SendCommitApproverTests
{
    [Test]
    public async Task UnavailableApproverReportsUnavailableNotDeclined()
    {
        // Unavailable (no interactive desktop) is an environment fact, distinct
        // from a human refusal; Core must surface it as such.
        var approver = new UnavailableSendCommitApprover();

        SendApprovalOutcome outcome = await approver.ApproveAsync(Preview(), CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(SendApprovalOutcome.Unavailable));
    }

#if DEBUG
    [Test]
    public async Task AutomaticApproverApprovesEverythingButIsDebugOnly()
    {
        // Compiles only in DEBUG: the type is excluded from Release binaries, so
        // production hosts cannot resolve it even by accident.
        var approver = new AutomaticSendCommitApprover();

        SendApprovalOutcome outcome = await approver.ApproveAsync(Preview(), CancellationToken.None);

        Assert.That(outcome, Is.EqualTo(SendApprovalOutcome.Approved));
    }
#endif

    private static SendPreview Preview() => new(
        "prep-contract",
        "personal",
        "<contract@example.test>",
        null,
        ["to@example.test"],
        [],
        [],
        SendMode.ConfirmDialog,
        null,
        null,
        0,
        [],
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(10),
        new string('a', 64),
        new string('b', 64),
        "token");
}
