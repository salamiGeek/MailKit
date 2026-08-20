using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;

namespace MailKit.Agent.Core.Tests.Sending;

public class SendApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);
    private const string SecretSubject = "Quarterly-Launch-Plan";

    [Test]
    public async Task PrepareReturnsRedactedPreviewAndTokenWithoutAcquiringSmtpOrCredential()
    {
        using var fixture = CreateFixture();

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-001", "session-a", CancellationToken.None);

        SendPreview preview = result.Data!;
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(preview.PreparationId, Is.Not.Null.And.Not.Empty);
            Assert.That(preview.AccountId, Is.EqualTo("personal"));
            Assert.That(preview.MessageId, Is.EqualTo("<message-1@example.com>"));
            Assert.That(preview.From, Is.EqualTo("Bob <user@example.com>"));
            Assert.That(preview.To, Is.EqualTo(new[] { "Alice <alice@example.com>" }));
            Assert.That(preview.Cc, Is.EqualTo(new[] { "carol@example.com" }));
            Assert.That(preview.Bcc, Is.EqualTo(new[] { "hidden@example.com" }));
            Assert.That(preview.Subject, Is.EqualTo(SecretSubject));
            Assert.That(preview.TextPreview, Is.EqualTo("Numbers attached."));
            Assert.That(preview.AttachmentCount, Is.EqualTo(1));
            Assert.That(preview.AttachmentNames, Is.EqualTo(new[] { "report.txt" }));
            Assert.That(preview.PreparedAt, Is.EqualTo(Now));
            Assert.That(preview.ExpiresAt, Is.EqualTo(Now.AddMinutes(10)));
            Assert.That(preview.ContentHash, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(preview.IdempotencyKeyHash, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(preview.ConfirmationToken, Is.Not.Null.And.Not.Empty);
            Assert.That(fixture.Composer.Calls, Is.EqualTo(1));
            Assert.That(fixture.Composer.LastIdempotencyKey, Is.EqualTo("idem-001"),
                "The composer must receive the raw idempotency key to derive the Message-Id.");
            Assert.That(fixture.Smtp.SendCount, Is.Zero);
            Assert.That(fixture.Vault.StatusCalls, Is.Zero);
            Assert.That(fixture.Vault.PasswordCalls, Is.Zero);
        });
    }

    [Test]
    public async Task PrepareAnnouncesTheConfirmDialogModeInThePreview()
    {
        using var fixture = CreateFixture();

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-mode", "session-a", CancellationToken.None);

        Assert.That(result.Data!.SendMode, Is.EqualTo(SendMode.ConfirmDialog));
    }

    [Test]
    public async Task DraftsPrepareRequiresImapBeforeComposing()
    {
        using var fixture = CreateFixture(store: new FakeAccountStore
        {
            Profile = DraftsProfile() with { Imap = null }
        });

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-drafts-noimap", "session-a", CancellationToken.None);

        AssertFailure(result, "imap.not_configured", ErrorCategory.Capability);
        Assert.That(fixture.Composer.Calls, Is.Zero,
            "The IMAP requirement must fail prepare before any composition work.");
    }

    [Test]
    public async Task DraftsPrepareWithoutSmtpStillSucceedsAndAnnouncesDraftsMode()
    {
        // The drafts mode never delivers over SMTP, so a missing SMTP endpoint
        // must not block preparing a draft for the Drafts folder.
        using var fixture = CreateFixture(store: new FakeAccountStore
        {
            Profile = DraftsProfile() with { Smtp = null }
        });

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-drafts", "session-a", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Error?.Message);
            Assert.That(result.Data!.SendMode, Is.EqualTo(SendMode.Drafts));
        });
    }

    [Test]
    public async Task DraftsCommitSkipsApprovalSavesOnceAndNeverDelivers()
    {
        using var fixture = CreateFixture(store: new FakeAccountStore { Profile = DraftsProfile() });
        var draft = Draft(fixture);
        CancellationToken token = CancellationToken.None;

        SendPreview preview = (await fixture.Application.PrepareAsync(
            "personal", draft, "idem-drafts-1", "session-a", token)).Data!;
        var expectedMime = fixture.Composer.Mime.ToArray();
        ToolResult<SendStatus> first = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", token);
        ToolResult<SendStatus> second = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", token);
        ToolResult<SendStatus> status = await fixture.Application.GetStatusAsync(
            "personal", "idem-drafts-1", token);

        Assert.Multiple(() =>
        {
            Assert.That(first.Ok, Is.True, first.Error?.Message);
            Assert.That(first.Data!.State, Is.EqualTo(SendState.Drafted));
            Assert.That(first.Data.AttemptedAt, Is.Not.Null);
            Assert.That(first.Data.CompletedAt, Is.Not.Null);
            Assert.That(second.Ok, Is.False,
                "The one-time token must never re-save the same draft.");
            Assert.That(fixture.DraftStore.SaveCount, Is.EqualTo(1));
            Assert.That(fixture.Smtp.SendCount, Is.Zero,
                "The drafts mode must never touch SMTP.");
            Assert.That(fixture.Approver.Seen, Is.Empty,
                "Nothing is delivered in drafts mode, so the local approval dialog must be skipped.");
            Assert.That(fixture.DraftStore.LastMessage!.AccountId, Is.EqualTo("personal"));
            Assert.That(fixture.DraftStore.LastMessage.MessageId, Is.EqualTo("<message-1@example.com>"));
            Assert.That(fixture.DraftStore.LastMimeSnapshot, Is.EqualTo(expectedMime));
            Assert.That(fixture.DraftStore.LastMessage.MimeMessage, Has.All.Zero,
                "MIME bytes must be zeroed after the draft is saved.");
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Drafted));
            Assert.That(fixture.Vault.LastLeaseIsDisposed, Is.True);
        });
    }

    [Test]
    public async Task DraftsCommitMapsTransportOutcomesToTerminalStates()
    {
        using var failedFixture = CreateFixture(
            store: new FakeAccountStore { Profile = DraftsProfile() },
            draftStore: new FakeDraftStore
            {
                Outcome = SendTransportOutcome.Failed(new ToolError(
                    "drafts.folder_not_found", ErrorCategory.Capability,
                    "The Drafts folder was not found.", false, null, null))
            });
        var failedPreview = await PrepareAsync(failedFixture, "idem-drafts-failed", "session-a");

        ToolResult<SendStatus> failed = await failedFixture.Application.CommitAsync(
            failedPreview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> failedStatus = await failedFixture.Application.GetStatusAsync(
            "personal", "idem-drafts-failed", CancellationToken.None);

        using var unknownFixture = CreateFixture(
            store: new FakeAccountStore { Profile = DraftsProfile() },
            draftStore: new FakeDraftStore { Outcome = SendTransportOutcome.Indeterminate() });
        var unknownPreview = await PrepareAsync(unknownFixture, "idem-drafts-unknown", "session-a");

        ToolResult<SendStatus> unknown = await unknownFixture.Application.CommitAsync(
            unknownPreview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> unknownStatus = await unknownFixture.Application.GetStatusAsync(
            "personal", "idem-drafts-unknown", CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertFailure(failed, "drafts.folder_not_found", ErrorCategory.Capability);
            Assert.That(failedStatus.Data!.State, Is.EqualTo(SendState.Failed));
            AssertFailure(unknown, "send.indeterminate", ErrorCategory.Transient);
            Assert.That(unknownStatus.Data!.State, Is.EqualTo(SendState.Indeterminate));
            Assert.That(failedFixture.DraftStore.SaveCount, Is.EqualTo(1));
            Assert.That(unknownFixture.DraftStore.SaveCount, Is.EqualTo(1));
            Assert.That(failedFixture.Smtp.SendCount, Is.Zero);
            Assert.That(unknownFixture.Smtp.SendCount, Is.Zero);
        });
    }

    [Test]
    public async Task CommitBranchesOnTheAccountModeResolvedAtCommitTime()
    {
        // The mode is re-read from the account store at commit time (like every
        // other account fact commit uses), so flipping the profile between the
        // two phases flips the execution branch.
        var store = new FakeAccountStore { Profile = DraftsProfile() };
        using var fixture = CreateFixture(store: store);
        SendPreview preview = await PrepareAsync(fixture, "idem-flip", "session-a");
        store.Profile = Profile();

        ToolResult<SendStatus> result = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True, result.Error?.Message);
            Assert.That(result.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(fixture.Smtp.SendCount, Is.EqualTo(1));
            Assert.That(fixture.DraftStore.SaveCount, Is.Zero);
            Assert.That(fixture.Approver.Seen, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task FirstCommitSucceedsAndRepeatedCommitFails()
    {
        using var fixture = CreateFixture();
        var draft = Draft(fixture);
        CancellationToken token = CancellationToken.None;
        SendApplication app = fixture.Application;
        FakeSmtpGateway fakeSmtp = fixture.Smtp;

        SendPreview preview = (await app.PrepareAsync("personal", draft,
            "idem-001", "session-a", token)).Data!;
        ToolResult<SendStatus> first = await app.CommitAsync(
            preview.ConfirmationToken, "session-a", token);
        ToolResult<SendStatus> second = await app.CommitAsync(
            preview.ConfirmationToken, "session-a", token);

        Assert.Multiple(() =>
        {
            Assert.That(first.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(second.Ok, Is.False);
            Assert.That(fakeSmtp.SendCount, Is.EqualTo(1));
            Assert.That(first.Data.MessageId, Is.EqualTo("<message-1@example.com>"));
            Assert.That(first.Data.IdempotencyKeyHash, Is.EqualTo(preview.IdempotencyKeyHash));
            Assert.That(first.Data.AttemptedAt, Is.Not.Null);
            Assert.That(first.Data.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task CommitDeliversBccOnlyInEnvelopeAndZeroesMimeBytesAfterwards()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-bcc", "session-a");
        var expectedMime = fixture.Composer.Mime.ToArray();

        await fixture.Application.CommitAsync(preview.ConfirmationToken, "session-a", CancellationToken.None);

        PreparedOutgoingMessage sent = fixture.Smtp.LastMessage!;
        Assert.Multiple(() =>
        {
            Assert.That(sent.EnvelopeSender, Is.EqualTo("user@example.com"));
            Assert.That(sent.EnvelopeRecipients, Is.EqualTo(new[]
            {
                "alice@example.com",
                "carol@example.com",
                "hidden@example.com"
            }));
            Assert.That(fixture.Smtp.LastMimeSnapshot, Is.EqualTo(expectedMime));
            Assert.That(sent.MimeMessage, Has.All.Zero, "MIME bytes must be zeroed after the send completes.");
            Assert.That(fixture.Vault.LastLeaseIsDisposed, Is.True);
        });
    }

    [Test]
    public async Task BccOnlyDraftDeliversThroughTheEnvelope()
    {
        // Core counts Bcc as recipients, and the composer accepts Bcc-only drafts;
        // the envelope must carry the blind-copy recipient while To/Cc stay empty.
        using var fixture = CreateFixture();
        var draft = Draft(fixture) with { To = [], Cc = [] };

        ToolResult<SendPreview> prepared = await fixture.Application.PrepareAsync(
            "personal", draft, "idem-bcc-only", "session-a", CancellationToken.None);
        Assert.That(prepared.Ok, Is.True, prepared.Error?.Message);
        await fixture.Application.CommitAsync(
            prepared.Data!.ConfirmationToken, "session-a", CancellationToken.None);

        PreparedOutgoingMessage sent = fixture.Smtp.LastMessage!;
        Assert.Multiple(() =>
        {
            Assert.That(prepared.Data.To, Is.Empty);
            Assert.That(prepared.Data.Cc, Is.Empty);
            Assert.That(prepared.Data.Bcc, Is.EqualTo(new[] { "hidden@example.com" }));
            Assert.That(sent.EnvelopeRecipients, Is.EqualTo(new[] { "hidden@example.com" }));
        });
    }

    [Test]
    public async Task CommitRejectsAlteredToken()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-altered", "session-a");
        var token = preview.ConfirmationToken;
        var separator = token.IndexOf('.');
        var replacement = token[separator + 1] == 'A' ? 'B' : 'A';
        var altered = token[..(separator + 1)] + replacement + token[(separator + 2)..];

        ToolResult<SendStatus> result = await fixture.Application.CommitAsync(
            altered, "session-a", CancellationToken.None);

        AssertFailure(result, "send.invalid_confirmation", ErrorCategory.Validation);
        Assert.That(fixture.Smtp.SendCount, Is.Zero);
        Assert.That(fixture.Vault.PasswordCalls, Is.Zero);
    }

    [Test]
    public async Task CommitRejectsWrongSession()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-session", "session-a");

        ToolResult<SendStatus> result = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-b", CancellationToken.None);

        AssertFailure(result, "send.session_mismatch", ErrorCategory.Validation);
        Assert.That(fixture.Smtp.SendCount, Is.Zero);
        Assert.That(fixture.Vault.PasswordCalls, Is.Zero);
    }

    [Test]
    public async Task CommitRejectsWrongAccountBinding()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-account", "session-a");
        var payload = fixture.Codec.Decode(preview.ConfirmationToken);
        var wrongAccount = fixture.Codec.Encode(payload with { AccountId = "other" });

        ToolResult<SendStatus> result = await fixture.Application.CommitAsync(
            wrongAccount, "session-a", CancellationToken.None);

        AssertFailure(result, "send.confirmation_mismatch", ErrorCategory.Validation);
        Assert.That(fixture.Smtp.SendCount, Is.Zero);
    }

    [Test]
    public async Task CommitRejectsContentHashMismatch()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-content", "session-a");
        var payload = fixture.Codec.Decode(preview.ConfirmationToken);
        var mismatched = fixture.Codec.Encode(payload with
        {
            ContentHash = new string('0', 64)
        });

        ToolResult<SendStatus> result = await fixture.Application.CommitAsync(
            mismatched, "session-a", CancellationToken.None);

        AssertFailure(result, "send.confirmation_mismatch", ErrorCategory.Validation);
        Assert.That(fixture.Smtp.SendCount, Is.Zero);
    }

    [Test]
    public async Task CommitRejectsExpiredConfirmation()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-expired", "session-a");
        fixture.Time.SetUtcNow(Now.AddMinutes(10));

        ToolResult<SendStatus> result = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        AssertFailure(result, "send.invalid_confirmation", ErrorCategory.Validation);
        Assert.That(fixture.Smtp.SendCount, Is.Zero);
    }

    [Test]
    public async Task RepeatedCommitAfterFailureDoesNotResend()
    {
        using var fixture = CreateFixture(
            smtpConfiguration: gateway => gateway.Outcome = SendTransportOutcome.Failed(new ToolError(
                "smtp.rejected", ErrorCategory.Transient, "The server rejected the message.",
                false, null, null)));
        var gateway = fixture.Smtp;
        var preview = await PrepareAsync(fixture, "idem-failed", "session-a");

        ToolResult<SendStatus> first = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> second = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> status = await fixture.Application.GetStatusAsync(
            "personal", "idem-failed", CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertFailure(first, "smtp.rejected", ErrorCategory.Transient);
            Assert.That(second.Ok, Is.False);
            Assert.That(gateway.SendCount, Is.EqualTo(1));
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Failed));
            Assert.That(fixture.Vault.LastLeaseIsDisposed, Is.True);
        });
    }

    [Test]
    public async Task IndeterminateTransportOutcomeIsTerminalAndNeverResent()
    {
        using var fixture = CreateFixture(
            smtpConfiguration: gateway => gateway.Outcome = SendTransportOutcome.Indeterminate());
        var gateway = fixture.Smtp;
        var preview = await PrepareAsync(fixture, "idem-unknown", "session-a");

        ToolResult<SendStatus> first = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> second = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> status = await fixture.Application.GetStatusAsync(
            "personal", "idem-unknown", CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertFailure(first, "send.indeterminate", ErrorCategory.Transient);
            Assert.That(second.Ok, Is.False);
            Assert.That(gateway.SendCount, Is.EqualTo(1));
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Indeterminate));
        });
    }

    [Test]
    public async Task CancellationAfterEnteringSmtpRecordsIndeterminate()
    {
        using var fixture = CreateFixture(
            smtpConfiguration: gateway => gateway.Exception = new OperationCanceledException());
        var gateway = fixture.Smtp;
        var preview = await PrepareAsync(fixture, "idem-cancel", "session-a");

        Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None));

        ToolResult<SendStatus> status = await fixture.Application.GetStatusAsync(
            "personal", "idem-cancel", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(gateway.SendCount, Is.EqualTo(1));
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Indeterminate));
            Assert.That(fixture.Vault.LastLeaseIsDisposed, Is.True);
        });
    }

    [Test]
    public async Task DeclinedApprovalDoesNotConsumeTokenOrSendOrWriteLedger()
    {
        // The local approval gate must run BEFORE the one-time token is consumed,
        // so a declined send keeps every retry right the caller already had.
        var approveNext = false;
        using var fixture = CreateFixture(approver: new FakeSendApprover((_, _) =>
        {
            bool decision = approveNext;
            approveNext = true;
            return Task.FromResult(decision
                ? SendApprovalOutcome.Approved
                : SendApprovalOutcome.Declined);
        }));

        SendPreview preview = await PrepareAsync(fixture, "idem-declined", "session-a");
        ToolResult<SendStatus> declined = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        int sendCountAfterDecline = fixture.Smtp.SendCount;
        int passwordCallsAfterDecline = fixture.Vault.PasswordCalls;
        ToolResult<SendStatus> statusAfterDecline = await fixture.Application.GetStatusAsync(
            "personal", "idem-declined", CancellationToken.None);
        ToolResult<SendStatus> retried = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertFailure(declined, "send.approval_declined", ErrorCategory.Validation);
            Assert.That(declined.Error!.Message, Is.EqualTo("The send was not approved locally."));
            Assert.That(declined.Error.Retryable, Is.False);
            Assert.That(sendCountAfterDecline, Is.Zero,
                "A declined approval must never reach SMTP.");
            Assert.That(passwordCallsAfterDecline, Is.Zero);
            Assert.That(statusAfterDecline.Data!.State, Is.EqualTo(SendState.Prepared),
                "A declined approval must not write Attempting or any terminal ledger state.");
            Assert.That(retried.Ok, Is.True, retried.Error?.Message);
            Assert.That(retried.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(fixture.Smtp.SendCount, Is.EqualTo(1),
                "The SAME one-time token must still deliver after a later approval is granted.");
        });
    }

    [Test]
    public async Task UnavailableApprovalMapsToStableCapabilityErrorWithoutConsuming()
    {
        // An environment without an interactive desktop is NOT a human refusal:
        // it must surface a distinct stable error so operators learn to run
        // interactively, and it must not consume the one-time token either.
        var unavailableOnce = true;
        using var fixture = CreateFixture(approver: new FakeSendApprover((_, _) =>
        {
            bool unavailable = unavailableOnce;
            unavailableOnce = false;
            return Task.FromResult(unavailable
                ? SendApprovalOutcome.Unavailable
                : SendApprovalOutcome.Approved);
        }));

        SendPreview preview = await PrepareAsync(fixture, "idem-unavailable", "session-a");
        ToolResult<SendStatus> unavailableResult = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        int sendCountAfterUnavailable = fixture.Smtp.SendCount;
        int passwordCallsAfterUnavailable = fixture.Vault.PasswordCalls;
        ToolResult<SendStatus> statusAfterUnavailable = await fixture.Application.GetStatusAsync(
            "personal", "idem-unavailable", CancellationToken.None);
        ToolResult<SendStatus> later = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertFailure(unavailableResult, "send.approval_unavailable", ErrorCategory.Capability);
            Assert.That(
                unavailableResult.Error!.Message,
                Is.EqualTo("Local human approval is unavailable in this environment."));
            Assert.That(unavailableResult.Error.Retryable, Is.False);
            Assert.That(sendCountAfterUnavailable, Is.Zero);
            Assert.That(passwordCallsAfterUnavailable, Is.Zero);
            Assert.That(statusAfterUnavailable.Data!.State, Is.EqualTo(SendState.Prepared),
                "An unavailable approval must not write Attempting or any terminal ledger state.");
            Assert.That(later.Ok, Is.True, later.Error?.Message);
            Assert.That(later.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(fixture.Smtp.SendCount, Is.EqualTo(1),
                "The SAME one-time token must still deliver once approval becomes available.");
        });
    }

    [Test]
    public async Task ApproverSeesTheExactPreparedPreview()
    {
        using var fixture = CreateFixture();
        SendPreview preview = await PrepareAsync(fixture, "idem-preview", "session-a");

        await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        SendPreview seen = fixture.Approver.Seen.Single();
        Assert.Multiple(() =>
        {
            Assert.That(seen.Subject, Is.EqualTo(SecretSubject));
            Assert.That(seen.From, Is.EqualTo("Bob <user@example.com>"));
            Assert.That(seen.To, Is.EqualTo(new[] { "Alice <alice@example.com>" }));
            Assert.That(seen.Cc, Is.EqualTo(new[] { "carol@example.com" }));
            Assert.That(seen.Bcc, Is.EqualTo(new[] { "hidden@example.com" }));
            Assert.That(seen.AttachmentCount, Is.EqualTo(1));
            Assert.That(seen.AttachmentNames, Is.EqualTo(new[] { "report.txt" }));
            Assert.That(seen.IdempotencyKeyHash, Is.EqualTo(preview.IdempotencyKeyHash));
            Assert.That(seen.ContentHash, Is.EqualTo(preview.ContentHash));
            Assert.That(seen.MessageId, Is.EqualTo("<message-1@example.com>"));
            Assert.That(seen.ExpiresAt, Is.EqualTo(preview.ExpiresAt));
        });
    }

    [Test]
    public async Task ApprovalHappensBeforeThePreparationIsConsumed()
    {
        var preparationStillStoredAtApproval = new List<bool>();
        using var fixture = CreateFixture();
        fixture.Approver.PreparationProbe = preview =>
        {
            preparationStillStoredAtApproval.Add(
                fixture.PreparedStore.TryGetAsync(preview.PreparationId, CancellationToken.None)
                    .GetAwaiter().GetResult() is not null);
            return true;
        };

        SendPreview preview = await PrepareAsync(fixture, "idem-order", "session-a");
        await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(preparationStillStoredAtApproval, Is.EqualTo(new[] { true }),
                "The preparation must still be stored while approval is pending: approval precedes TakeAsync.");
            Assert.That(fixture.Smtp.SendCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CancellationWhileWaitingForApprovalDoesNotPoisonTheLedger()
    {
        var cancelledOnce = false;
        using var fixture = CreateFixture(approver: new FakeSendApprover(
            (_, cancellationToken) =>
            {
                if (cancelledOnce)
                    return Task.FromResult(SendApprovalOutcome.Approved);
                cancelledOnce = true;
                return Task.FromException<SendApprovalOutcome>(
                    new OperationCanceledException(cancellationToken));
            }));
        SendPreview preview = await PrepareAsync(fixture, "idem-cancel-approval", "session-a");

        Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None));
        int sendCountAfterCancellation = fixture.Smtp.SendCount;

        ToolResult<SendStatus> status = await fixture.Application.GetStatusAsync(
            "personal", "idem-cancel-approval", CancellationToken.None);
        ToolResult<SendStatus> later = await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Prepared),
                "Cancellation during approval must not write Attempting or Indeterminate.");
            Assert.That(sendCountAfterCancellation, Is.Zero);
            Assert.That(later.Ok, Is.True, later.Error?.Message);
            Assert.That(fixture.Smtp.SendCount, Is.EqualTo(1),
                "The token stays usable after a cancelled approval wait.");
        });
    }

    [Test]
    public async Task AttemptingFromEarlierProcessIsIndeterminateAndNeverInvokesSmtp()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-restart", "session-a");
        await fixture.Ledger.TransitionAsync(
            "personal", preview.IdempotencyKeyHash, SendState.Attempting,
            Now.AddSeconds(5), "earlier-process", CancellationToken.None);

        var restartedFixture = CreateFixture(
            ledger: new JsonSendLedger(fixture.Temp.Path),
            preparedStore: fixture.PreparedStore,
            composer: fixture.Composer,
            smtp: fixture.Smtp,
            vault: fixture.Vault,
            store: fixture.Store);

        ToolResult<SendStatus> commit = await restartedFixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);
        ToolResult<SendStatus> status = await restartedFixture.Application.GetStatusAsync(
            "personal", "idem-restart", CancellationToken.None);

        Assert.Multiple(() =>
        {
            AssertFailure(commit, "send.idempotency_conflict", ErrorCategory.Conflict);
            Assert.That(fixture.Smtp.SendCount, Is.Zero);
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Indeterminate));
        });
    }

    [Test]
    public async Task ConcurrentSameKeyCommitsInvokeSmtpExactlyOnce()
    {
        using var fixture = CreateFixture();
        var first = await PrepareAsync(fixture, "idem-race", "session-a");
        var second = await PrepareAsync(fixture, "idem-race", "session-a");
        Assert.That(second.ConfirmationToken, Is.Not.EqualTo(first.ConfirmationToken),
            "The two preparations must hold distinct one-time confirmations.");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = new[]
        {
            Task.Run(async () =>
            {
                await start.Task;
                return await fixture.Application.CommitAsync(
                    first.ConfirmationToken, "session-a", CancellationToken.None);
            }),
            Task.Run(async () =>
            {
                await start.Task;
                return await fixture.Application.CommitAsync(
                    second.ConfirmationToken, "session-a", CancellationToken.None);
            })
        };
        start.SetResult();
        var results = await Task.WhenAll(commits);

        ToolResult<SendStatus> winner = results.Single(result => result.Ok);
        ToolResult<SendStatus> loser = results.Single(result => !result.Ok);
        ToolResult<SendStatus> status = await fixture.Application.GetStatusAsync(
            "personal", "idem-race", CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Smtp.SendCount, Is.EqualTo(1),
                "A single idempotency key must never be delivered twice, even under concurrent commits.");
            Assert.That(winner.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(loser.Error!.Code, Is.EqualTo("send.idempotency_conflict"));
            Assert.That(loser.Error.Category, Is.EqualTo(ErrorCategory.Conflict));
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Succeeded));
        });
    }

    [Test]
    public async Task TokenBindsIdentityWithoutMessageContent()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-token", "session-a");

        var payloadSegment = preview.ConfirmationToken[..preview.ConfirmationToken.IndexOf('.')];
        using var document = JsonDocument.Parse(Base64UrlDecode(payloadSegment));
        var propertyNames = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        var rawPayload = document.RootElement.GetRawText();

        Assert.Multiple(() =>
        {
            Assert.That(propertyNames, Is.EquivalentTo(new[]
            {
                "preparation_id",
                "account_id",
                "content_hash",
                "idempotency_key_hash",
                "session_id",
                "expires_at"
            }));
            Assert.That(rawPayload, Does.Not.Contain(SecretSubject));
            Assert.That(rawPayload, Does.Not.Contain("alice@example.com"));
            Assert.That(rawPayload, Does.Not.Contain("hidden@example.com"));
            Assert.That(rawPayload, Does.Not.Contain("report.txt"));
            Assert.That(rawPayload, Does.Not.Contain("Numbers attached."));
        });
    }

    [Test]
    public async Task GetStatusReturnsUnknownKeyFailureWithoutEchoingRawKey()
    {
        using var fixture = CreateFixture();

        ToolResult<SendStatus> result = await fixture.Application.GetStatusAsync(
            "personal", "idem-404", CancellationToken.None);

        AssertFailure(result, "send.status_not_found", ErrorCategory.Validation);
        Assert.That(result.Error!.Message, Does.Not.Contain("idem-404"));
    }

    [Test]
    public async Task PrepareRejectsMissingRecipients()
    {
        using var fixture = CreateFixture();
        var draft = Draft(fixture) with { To = [], Cc = [], Bcc = [] };

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", draft, "idem-001", "session-a", CancellationToken.None);

        AssertFailure(result, "validation.missing_recipients", ErrorCategory.Validation);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [Test]
    public async Task PrepareRejectsInvalidRecipientAddressSyntax()
    {
        using var fixture = CreateFixture();
        var draft = Draft(fixture) with { To = [new OutgoingMailbox("Alice", "not-an-address")] };

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", draft, "idem-001", "session-a", CancellationToken.None);

        AssertFailure(result, "validation.invalid_recipient", ErrorCategory.Validation);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [Test]
    public async Task PrepareRejectsOversizedSubjectAndBody()
    {
        using var fixture = CreateFixture();
        var oversizedSubject = Draft(fixture) with { Subject = new string('s', 999) };
        var oversizedBody = Draft(fixture) with
        {
            TextBody = new string('b', MailSafetyLimits.Default.MaxBodyCharacters + 1)
        };

        ToolResult<SendPreview> subjectResult = await fixture.Application.PrepareAsync(
            "personal", oversizedSubject, "idem-001", "session-a", CancellationToken.None);
        ToolResult<SendPreview> bodyResult = await fixture.Application.PrepareAsync(
            "personal", oversizedBody, "idem-002", "session-a", CancellationToken.None);

        AssertFailure(subjectResult, "validation.subject_too_long", ErrorCategory.Validation);
        AssertFailure(bodyResult, "validation.body_too_long", ErrorCategory.Validation);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [Test]
    public async Task PrepareRejectsAttachmentOutsideConfiguredRoots()
    {
        using var fixture = CreateFixture();
        var outsidePath = Path.Combine(fixture.Temp.Path, "outside", "secret.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outsidePath)!);
        await File.WriteAllTextAsync(outsidePath, "outside");
        var missing = Draft(fixture) with { AttachmentPaths = [Path.Combine(fixture.Temp.Path, "missing.txt")] };
        var outside = Draft(fixture) with { AttachmentPaths = [outsidePath] };

        ToolResult<SendPreview> missingResult = await fixture.Application.PrepareAsync(
            "personal", missing, "idem-001", "session-a", CancellationToken.None);
        ToolResult<SendPreview> outsideResult = await fixture.Application.PrepareAsync(
            "personal", outside, "idem-002", "session-a", CancellationToken.None);

        AssertFailure(missingResult, "validation.attachment_not_found", ErrorCategory.Validation);
        AssertFailure(outsideResult, "validation.attachment_outside_root", ErrorCategory.Validation);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("spaces in key")]
    [TestCase("key/with/slashes")]
    public async Task PrepareRejectsInvalidIdempotencyKeyFormat(string key)
    {
        using var fixture = CreateFixture();

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), key, "session-a", CancellationToken.None);

        AssertFailure(result, "validation.invalid_idempotency_key", ErrorCategory.Validation);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [Test]
    public async Task PrepareRejectsBlankSession()
    {
        using var fixture = CreateFixture();

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-001", " ", CancellationToken.None);

        AssertFailure(result, "validation.invalid_session", ErrorCategory.Validation);
    }

    [Test]
    public async Task PrepareRejectsMissingSmtpEndpoint()
    {
        using var fixture = CreateFixture(
            store: new FakeAccountStore { Profile = Profile() with { Smtp = null } });

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-001", "session-a", CancellationToken.None);

        AssertFailure(result, "smtp.not_configured", ErrorCategory.Capability);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [Test]
    public async Task PrepareRejectsUnknownAccountBeforeComposerAccess()
    {
        using var fixture = CreateFixture(store: new FakeAccountStore { Profile = null });

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "missing", Draft(fixture), "idem-001", "session-a", CancellationToken.None);

        AssertFailure(result, "account.not_found", ErrorCategory.Validation);
        Assert.That(fixture.Composer.Calls, Is.Zero);
    }

    [Test]
    public async Task PrepareRejectsWhenTerminalLedgerRecordExists()
    {
        using var fixture = CreateFixture();
        var preview = await PrepareAsync(fixture, "idem-001", "session-a");
        await fixture.Application.CommitAsync(
            preview.ConfirmationToken, "session-a", CancellationToken.None);

        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), "idem-001", "session-a", CancellationToken.None);

        AssertFailure(result, "send.idempotency_conflict", ErrorCategory.Conflict);
        Assert.That(fixture.Composer.Calls, Is.EqualTo(1));
    }

    private static async Task<SendPreview> PrepareAsync(
        Fixture fixture, string idempotencyKey, string sessionId)
    {
        ToolResult<SendPreview> result = await fixture.Application.PrepareAsync(
            "personal", Draft(fixture), idempotencyKey, sessionId, CancellationToken.None);

        Assert.That(result.Ok, Is.True, result.Error?.Message);
        return result.Data!;
    }

    private static OutgoingMessageDraft Draft(Fixture fixture) => new(
        [new OutgoingMailbox("Alice", "alice@example.com")],
        [new OutgoingMailbox(null, "carol@example.com")],
        [new OutgoingMailbox(null, "hidden@example.com")],
        new OutgoingMailbox("Bob", "user@example.com"),
        SecretSubject,
        "Numbers attached.",
        null,
        [fixture.AttachmentPath]);

    private static AccountProfile Profile() => new(
        "personal", "Personal", "user@example.com", AuthenticationKind.Password,
        new EndpointSettings("imap.example.com", 993, TlsMode.ImplicitTls),
        null,
        new EndpointSettings("smtp.example.com", 465, TlsMode.ImplicitTls));

    private static AccountProfile DraftsProfile() => Profile() with { SendMode = SendMode.Drafts };

    private static Fixture CreateFixture(
        IAccountProfileStore? store = null,
        FakeVault? vault = null,
        FakeComposer? composer = null,
        FakeSmtpGateway? smtp = null,
        ISendLedger? ledger = null,
        IPreparedSendStore? preparedStore = null,
        Action<FakeSmtpGateway>? smtpConfiguration = null,
        FakeSendApprover? approver = null,
        FakeDraftStore? draftStore = null)
    {
        var smtpGateway = smtp ?? new FakeSmtpGateway();
        smtpConfiguration?.Invoke(smtpGateway);
        return new Fixture(
            store ?? new FakeAccountStore { Profile = Profile() },
            vault ?? new FakeVault(),
            composer ?? new FakeComposer(),
            smtpGateway,
            ledger,
            preparedStore,
            approver ?? new FakeSendApprover(),
            draftStore ?? new FakeDraftStore());
    }

    private static void AssertFailure<T>(ToolResult<T> result, string code, ErrorCategory category)
    {
        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo(code));
            Assert.That(result.Error.Category, Is.EqualTo(category));
        });
    }

    private static string Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(
            IAccountProfileStore store,
            FakeVault vault,
            FakeComposer composer,
            FakeSmtpGateway smtp,
            ISendLedger? ledger,
            IPreparedSendStore? preparedStore,
            FakeSendApprover approver,
            FakeDraftStore draftStore)
        {
            Temp = new TemporaryDirectory();
            Time = new MutableTimeProvider(Now);
            Codec = new HmacSendConfirmationCodec(
                SHA256.HashData("send-application-test-key"u8.ToArray()), Time);
            Store = store;
            Vault = vault;
            Composer = composer;
            Smtp = smtp;
            Approver = approver;
            DraftStore = draftStore;
            PreparedStore = preparedStore ?? new MemoryPreparedSendStore(Time);
            Ledger = ledger ?? new JsonSendLedger(Temp.Path);

            Directory.CreateDirectory(Path.Combine(Temp.Path, "uploads"));
            AttachmentPath = Path.Combine(Temp.Path, "uploads", "report.txt");
            File.WriteAllText(AttachmentPath, "attachment payload");

            Application = new SendApplication(
                Store,
                Vault,
                Composer,
                Smtp,
                DraftStore,
                Codec,
                PreparedStore,
                Ledger,
                Approver,
                OperationPolicy.Default,
                MailSafetyLimits.Default,
                Time,
                confirmationTtl: null,
                mailFileOptions: new MailFileOptions(
                    Path.Combine(Temp.Path, "downloads"),
                    [Path.Combine(Temp.Path, "uploads")]));
        }

        public TemporaryDirectory Temp { get; }
        public MutableTimeProvider Time { get; }
        public HmacSendConfirmationCodec Codec { get; }
        public IAccountProfileStore Store { get; }
        public FakeVault Vault { get; }
        public FakeComposer Composer { get; }
        public FakeSmtpGateway Smtp { get; }
        public FakeSendApprover Approver { get; }
        public FakeDraftStore DraftStore { get; }
        public IPreparedSendStore PreparedStore { get; }
        public ISendLedger Ledger { get; }
        public SendApplication Application { get; }
        public string AttachmentPath { get; }

        public void Dispose() => Temp.Dispose();
    }

    /// <summary>
    /// Permissive recording approver so each test states explicitly that local
    /// approval was granted; production hosts never inject an automatic approver.
    /// </summary>
    private sealed class FakeSendApprover(
        Func<SendPreview, CancellationToken, Task<SendApprovalOutcome>>? handler = null) : ISendCommitApprover
    {
        private readonly Func<SendPreview, CancellationToken, Task<SendApprovalOutcome>>? handler = handler;

        public List<SendPreview> Seen { get; } = [];

        public Func<SendPreview, bool>? PreparationProbe { get; set; }

        public async ValueTask<SendApprovalOutcome> ApproveAsync(
            SendPreview preview, CancellationToken cancellationToken)
        {
            Seen.Add(preview);
            if (PreparationProbe?.Invoke(preview) == false)
                return SendApprovalOutcome.Declined;
            if (handler is null)
                return SendApprovalOutcome.Approved;
            return await handler(preview, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }

    private sealed class FakeAccountStore : IAccountProfileStore
    {
        public AccountProfile? Profile { get; set; }
        public Task<AccountProfile?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Profile is not null && Profile.Id == id ? Profile : null);
        public Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AccountProfile>>(Profile is null ? [] : [Profile]);
        public Task PutAsync(AccountProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeVault : IAccountCredentialVault
    {
        private PasswordCredentialLease? lastLease;
        public bool Configured { get; init; } = true;
        public int StatusCalls { get; private set; }
        public int PasswordCalls { get; private set; }
        public bool LastLeaseIsDisposed
        {
            get
            {
                if (lastLease is null)
                    return false;
                try
                {
                    lastLease.CreateNetworkCredential("probe");
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }
        }

        public ValueTask<CredentialStatus> GetStatusAsync(string accountId, CancellationToken cancellationToken)
        {
            StatusCalls++;
            return ValueTask.FromResult(new CredentialStatus(Configured, Configured ? CredentialKind.Password : null));
        }

        public ValueTask<PasswordCredentialLease> GetPasswordAsync(string accountId, CancellationToken cancellationToken)
        {
            PasswordCalls++;
            lastLease = PasswordCredentialLease.FromCharacters("private-password");
            return ValueTask.FromResult(lastLease);
        }

        public ValueTask SetPasswordAsync(string accountId, string username, ReadOnlyMemory<char> password, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<bool> DeletePasswordAsync(string accountId, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class FakeComposer : IOutgoingMessageComposer
    {
        public const string MessageId = "<message-1@example.com>";
        public byte[] Mime { get; } = "MIME without Bcc headers"u8.ToArray();
        public int Calls { get; private set; }
        public string? LastIdempotencyKey { get; private set; }
        public AccountProfile? LastProfile { get; private set; }
        public Exception? Exception { get; set; }

        public Task<ComposedOutgoingMessage> ComposeAsync(
            AccountProfile profile, OutgoingMessageDraft draft, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastProfile = profile;
            LastIdempotencyKey = idempotencyKey;
            if (Exception is not null)
                return Task.FromException<ComposedOutgoingMessage>(Exception);
            return Task.FromResult(new ComposedOutgoingMessage(Mime, MessageId));
        }
    }

    private sealed class FakeSmtpGateway : ISmtpGateway
    {
        public SendTransportOutcome Outcome { get; set; } = SendTransportOutcome.Succeeded();
        public Exception? Exception { get; set; }
        public int SendCount { get; private set; }
        public PreparedOutgoingMessage? LastMessage { get; private set; }
        public byte[]? LastMimeSnapshot { get; private set; }
        public PasswordCredentialLease? LastCredential { get; private set; }
        public AccountProfile? LastProfile { get; private set; }

        public Task<SendTransportOutcome> SendAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            PreparedOutgoingMessage message,
            CancellationToken cancellationToken)
        {
            SendCount++;
            LastProfile = profile;
            LastCredential = credential;
            LastMessage = message;
            LastMimeSnapshot = message.MimeMessage.ToArray();
            if (Exception is not null)
                return Task.FromException<SendTransportOutcome>(Exception);
            return Task.FromResult(Outcome);
        }
    }

    private sealed class FakeDraftStore : IDraftMessageStore
    {
        public SendTransportOutcome Outcome { get; set; } = SendTransportOutcome.Succeeded();
        public int SaveCount { get; private set; }
        public PreparedOutgoingMessage? LastMessage { get; private set; }
        public byte[]? LastMimeSnapshot { get; private set; }
        public AccountProfile? LastProfile { get; private set; }

        public Task<SendTransportOutcome> SaveAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            PreparedOutgoingMessage message,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            LastProfile = profile;
            LastMessage = message;
            LastMimeSnapshot = message.MimeMessage.ToArray();
            return Task.FromResult(Outcome);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"mailkit-agent-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public class MemoryPreparedSendStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 4, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task TakeRemovesPreparationAtomically()
    {
        var time = new MutableTimeProvider(Now);
        using var store = new MemoryPreparedSendStore(time);
        var message = Message(preparationId: "prep-1", expiresAt: Now.AddMinutes(10));
        await store.AddAsync(message, CancellationToken.None);

        var first = await store.TakeAsync("prep-1", CancellationToken.None);
        var second = await store.TakeAsync("prep-1", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(message));
            Assert.That(second, Is.Null);
        });
    }

    [Test]
    public async Task TakeReturnsNullForUnknownPreparation()
    {
        using var store = new MemoryPreparedSendStore(new MutableTimeProvider(Now));

        var taken = await store.TakeAsync("unknown", CancellationToken.None);

        Assert.That(taken, Is.Null);
    }

    [Test]
    public async Task ExpiredPreparationIsClearedAndItsMimeBytesZeroed()
    {
        var time = new MutableTimeProvider(Now);
        using var store = new MemoryPreparedSendStore(time);
        var message = Message(preparationId: "prep-expired", expiresAt: Now.AddMinutes(10));
        await store.AddAsync(message, CancellationToken.None);
        time.SetUtcNow(Now.AddMinutes(10));

        var taken = await store.TakeAsync("prep-expired", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(taken, Is.Null);
            Assert.That(message.MimeMessage, Has.All.Zero);
        });
    }

    [Test]
    public async Task TryGetReturnsPreparationWithoutConsumingIt()
    {
        var time = new MutableTimeProvider(Now);
        using var store = new MemoryPreparedSendStore(time);
        var message = Message(preparationId: "prep-peek", expiresAt: Now.AddMinutes(10));
        await store.AddAsync(message, CancellationToken.None);

        var firstPeek = await store.TryGetAsync("prep-peek", CancellationToken.None);
        var secondPeek = await store.TryGetAsync("prep-peek", CancellationToken.None);
        var taken = await store.TakeAsync("prep-peek", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(firstPeek, Is.EqualTo(message));
            Assert.That(secondPeek, Is.EqualTo(message),
                "Peeking must be repeatable without consuming the preparation.");
            Assert.That(taken, Is.EqualTo(message),
                "A peek must leave the preparation available to a later TakeAsync.");
        });
    }

    [Test]
    public async Task TryGetReturnsNullForUnknownPreparation()
    {
        using var store = new MemoryPreparedSendStore(new MutableTimeProvider(Now));

        var peeked = await store.TryGetAsync("unknown", CancellationToken.None);

        Assert.That(peeked, Is.Null);
    }

    [Test]
    public async Task TryGetSweepsExpiredPreparationsLikeTake()
    {
        var time = new MutableTimeProvider(Now);
        using var store = new MemoryPreparedSendStore(time);
        var message = Message(preparationId: "prep-peek-expired", expiresAt: Now.AddMinutes(10));
        await store.AddAsync(message, CancellationToken.None);
        time.SetUtcNow(Now.AddMinutes(10));

        var peeked = await store.TryGetAsync("prep-peek-expired", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(peeked, Is.Null);
            Assert.That(message.MimeMessage, Has.All.Zero,
                "An expired preparation must be swept and zeroed even when only peeked.");
        });
    }

    private static PreparedOutgoingMessage Message(string preparationId, DateTimeOffset expiresAt) => new(
        preparationId,
        "personal",
        "<message-1@example.com>",
        new string('a', 64),
        "mime bytes"u8.ToArray(),
        "user@example.com",
        ["alice@example.com"],
        Preview(preparationId),
        new string('b', 64),
        expiresAt);

    private static SendPreview Preview(string preparationId) => new(
        preparationId,
        "personal",
        "<message-1@example.com>",
        null,
        [],
        [],
        [],
        SendMode.ConfirmDialog,
        null,
        null,
        0,
        [],
        Now,
        Now.AddMinutes(10),
        new string('a', 64),
        new string('b', 64),
        "token");

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public void SetUtcNow(DateTimeOffset value) => utcNow = value;
    }
}
