using System.Text;
using System.Text.Json;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Core.Storage;
using MailKit.Agent.Mcp.Tools;

namespace MailKit.Agent.Mcp.Tests.Tools;

public class SendToolsTests
{
    private const string AccountId = "work";
    private const string IdempotencyKey = "fixture-key-1";

    [Test]
    public async Task PrepareBindsProcessSessionAndReturnsRedactedPreviewWithToken()
    {
        const string sensitiveMarker = "private-body-marker";
        var harness = CreateHarness();
        string longBody = new string('a', 300) + sensitiveMarker;
        var request = new SendPrepareRequest(
            AccountId,
            new OutgoingMessageDraft(
                To: [new OutgoingMailbox(null, "to@example.test")],
                Cc: null,
                Bcc: null,
                From: null,
                Subject: "Fixture subject",
                TextBody: longBody,
                HtmlBody: null,
                AttachmentPaths: null),
            IdempotencyKey);

        var result = await SendTools.PrepareAsync(
            request, server: null, new StdioSessionIdentity(), harness.Application, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data!.ConfirmationToken, Is.Not.Null.And.Not.Empty);
            Assert.That(result.Data.ConfirmationToken, Does.Contain("."));
            Assert.That(result.Data.TextPreview, Has.Length.EqualTo(SendApplication.TextPreviewLength));
            var json = JsonSerializer.Serialize(result);
            Assert.That(json, Does.Not.Contain(sensitiveMarker));
            Assert.That(harness.Composer.ComposeCalls, Has.Count.EqualTo(1));
            Assert.That(harness.Smtp.SendCount, Is.Zero);
        });
    }

    [Test]
    public async Task CommitConsumesOneTimeTokenAndDeliversExactlyOnce()
    {
        var harness = CreateHarness();
        var identity = new StdioSessionIdentity();
        var prepared = await SendTools.PrepareAsync(
            PrepareRequest(), server: null, identity, harness.Application, CancellationToken.None);
        Assert.That(prepared.Ok, Is.True);

        var committed = await SendTools.CommitAsync(
            new SendCommitRequest(prepared.Data!.ConfirmationToken),
            server: null,
            identity,
            harness.Application,
            CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(committed.Ok, Is.True);
            Assert.That(committed.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(harness.Smtp.SendCount, Is.EqualTo(1));
        });

        var repeated = await SendTools.CommitAsync(
            new SendCommitRequest(prepared.Data!.ConfirmationToken),
            server: null,
            identity,
            harness.Application,
            CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(repeated.Ok, Is.False);
            Assert.That(
                repeated.Error!.Code,
                Is.AnyOf("send.preparation_not_found", "send.idempotency_conflict"));
            Assert.That(harness.Smtp.SendCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CommitRequiresAValidConfirmation()
    {
        var harness = CreateHarness();

        var result = await SendTools.CommitAsync(
            new SendCommitRequest("not-a-confirmation-token"),
            server: null,
            new StdioSessionIdentity(),
            harness.Application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo("send.invalid_confirmation"));
            Assert.That(harness.Smtp.SendCount, Is.Zero);
        });
    }

    [Test]
    public async Task CommitRejectsTokensIssuedToAnotherSession()
    {
        var harness = CreateHarness();
        var prepared = await SendTools.PrepareAsync(
            PrepareRequest(),
            server: null,
            new StdioSessionIdentity(),
            harness.Application,
            CancellationToken.None);
        Assert.That(prepared.Ok, Is.True);

        var result = await SendTools.CommitAsync(
            new SendCommitRequest(prepared.Data!.ConfirmationToken),
            server: null,
            new StdioSessionIdentity(),
            harness.Application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False);
            Assert.That(result.Error!.Code, Is.EqualTo("send.session_mismatch"));
            Assert.That(harness.Smtp.SendCount, Is.Zero);
        });
    }

    [Test]
    public async Task StatusReportsTheDurableTerminalState()
    {
        var harness = CreateHarness();
        var identity = new StdioSessionIdentity();
        var prepared = await SendTools.PrepareAsync(
            PrepareRequest(), server: null, identity, harness.Application, CancellationToken.None);
        await SendTools.CommitAsync(
            new SendCommitRequest(prepared.Data!.ConfirmationToken),
            server: null,
            identity,
            harness.Application,
            CancellationToken.None);

        var status = await SendTools.GetStatusAsync(
            new SendStatusRequest(AccountId, IdempotencyKey),
            harness.Application,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(status.Ok, Is.True);
            Assert.That(status.Data!.State, Is.EqualTo(SendState.Succeeded));
            Assert.That(status.Data.MessageId, Is.EqualTo("fixture-message-id@example.test"));
        });
    }

    [Test]
    public async Task ConfirmationTokenPayloadCarriesSessionIdWithoutSecretShapedFields()
    {
        var harness = CreateHarness();
        var identity = new StdioSessionIdentity();
        var prepared = await SendTools.PrepareAsync(
            PrepareRequest(), server: null, identity, harness.Application, CancellationToken.None);
        Assert.That(prepared.Ok, Is.True);

        string payloadJson = DecodeConfirmationTokenPayload(prepared.Data!.ConfirmationToken);
        using var payload = JsonDocument.Parse(payloadJson);

        Assert.Multiple(() =>
        {
            // The stdio session identity IS client-visible inside the signed payload;
            // that is the documented invariant, so assert it explicitly rather than
            // assume secrecy.
            Assert.That(
                payload.RootElement.GetProperty("session_id").GetString(),
                Is.EqualTo(identity.Id));
            Assert.That(
                payload.RootElement.EnumerateObject().Select(property => property.Name),
                Has.None.Match(
                    "password|passwd|token|secret|credential_value|authorization"));
            Assert.That(payloadJson, Does.Not.Contain("fixture-password"));
            Assert.That(payloadJson, Does.Not.Contain("Fixture body"));
        });
        // The capability boundary stays closed: a token issued to this session is
        // useless to another one (also covered by CommitRejectsTokensIssuedToAnotherSession).
        var stolen = await SendTools.CommitAsync(
            new SendCommitRequest(prepared.Data.ConfirmationToken),
            server: null,
            new StdioSessionIdentity(),
            harness.Application,
            CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(stolen.Ok, Is.False);
            Assert.That(stolen.Error!.Code, Is.EqualTo("send.session_mismatch"));
            Assert.That(harness.Smtp.SendCount, Is.Zero);
        });
    }

    private static string DecodeConfirmationTokenPayload(string token)
    {
        string[] parts = token.Split('.');
        Assert.That(parts.Length, Is.EqualTo(2), "Token must be payload.signature.");
        string base64 = parts[0].Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new FormatException("Invalid base64url length.")
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private static SendPrepareRequest PrepareRequest() => new(
        AccountId,
        new OutgoingMessageDraft(
            To: [new OutgoingMailbox(null, "to@example.test")],
            Cc: null,
            Bcc: null,
            From: null,
            Subject: "Fixture subject",
            TextBody: "Fixture body.",
            HtmlBody: null,
            AttachmentPaths: null),
        IdempotencyKey);

    private static Harness CreateHarness()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "mailkit-agent-send-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        var store = new ConnectionToolsTests.InMemoryStore();
        store.PutAsync(ConnectionToolsTests.CreateProfile(AccountId), CancellationToken.None)
            .GetAwaiter().GetResult();

        var composer = new RecordingComposer();
        var smtp = new CountingSmtpGateway();
        var application = new SendApplication(
            store,
            new ConnectionToolsTests.FakeVault(),
            composer,
            smtp,
            new HmacSendConfirmationCodec(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
                TimeProvider.System),
            new MemoryPreparedSendStore(),
            new JsonSendLedger(dataDirectory),
            OperationPolicy.Default,
            MailSafetyLimits.Default,
            TimeProvider.System,
            mailFileOptions: new MailFileOptions(
                Path.Combine(dataDirectory, "downloads"),
                Array.Empty<string>()));

        return new Harness(application, composer, smtp, dataDirectory);
    }

    private sealed class Harness(
        SendApplication application,
        RecordingComposer composer,
        CountingSmtpGateway smtp,
        string dataDirectory) : IDisposable
    {
        public SendApplication Application { get; } = application;

        public RecordingComposer Composer { get; } = composer;

        public CountingSmtpGateway Smtp { get; } = smtp;

        public void Dispose()
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed class RecordingComposer : IOutgoingMessageComposer
    {
        public List<OutgoingMessageDraft> ComposeCalls { get; } = [];

        public Task<ComposedOutgoingMessage> ComposeAsync(
            AccountProfile profile,
            OutgoingMessageDraft draft,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            ComposeCalls.Add(draft);
            return Task.FromResult(new ComposedOutgoingMessage(
                [1, 2, 3],
                "fixture-message-id@example.test"));
        }
    }

    private sealed class CountingSmtpGateway : ISmtpGateway
    {
        public int SendCount { get; private set; }

        public Task<SendTransportOutcome> SendAsync(
            AccountProfile profile,
            PasswordCredentialLease credential,
            PreparedOutgoingMessage message,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(SendTransportOutcome.Succeeded());
        }
    }
}
