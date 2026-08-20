using MailKit;
using MailKit.Agent.Core.Accounts;
using MailKit.Agent.Core.Credentials;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Sending;
using MailKit.Agent.Mail.Connections;
using MailKit.Net.Imap;
using MimeKit;

namespace MailKit.Agent.Mail.Imap;

/// <summary>
/// Saves exactly one prepared message into the account's IMAP Drafts folder and
/// reports a conservative terminal outcome. The store never delivers anything: the
/// human later reviews, possibly modifies, and sends the draft manually from their
/// own mail client. The Drafts folder is resolved 1) via the server's SPECIAL-USE
/// (<c>\Drafts</c>) attribute when advertised, 2) otherwise by scanning the personal
/// namespaces for a folder named <c>"Drafts"</c> or <c>"草稿箱"</c>
/// (OrdinalIgnoreCase, the latter matching QQ Mail); when neither exists the store
/// fails with the stable capability error <c>drafts.folder_not_found</c>. The single
/// APPEND carries the <see cref="MessageFlags.Draft"/> flag and the preparation
/// timestamp; once the APPEND has started, ambiguous transport failures (I/O
/// errors, timeouts, protocol disconnects) are reported as
/// <see cref="SendState.Indeterminate"/> and the store never reconnects or retries;
/// a non-throwing disconnect always runs in the cleanup path. The MIME bytes are the
/// prepared payload (they never carry Bcc headers) and remain owned — and are zeroed
/// — by the calling send application.
/// </summary>
public sealed class ImapDraftStore : IDraftMessageStore
{
	private readonly IImapClientFactory clientFactory;
	private readonly ConnectionGate connectionGate;
	private readonly TimeSpan commandTimeout;

	public ImapDraftStore(
		IImapClientFactory clientFactory,
		TimeSpan? commandTimeout = null,
		ConnectionGate? connectionGate = null)
	{
		this.clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
		this.connectionGate = connectionGate ?? new ConnectionGate();
		this.commandTimeout = commandTimeout ?? ConnectionLimits.Default.CommandTimeout;
		if (this.commandTimeout <= TimeSpan.Zero)
			throw new ArgumentOutOfRangeException(nameof(commandTimeout));
	}

	public async Task<SendTransportOutcome> SaveAsync(
		AccountProfile profile,
		PasswordCredentialLease credential,
		PreparedOutgoingMessage message,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(credential);
		ArgumentNullException.ThrowIfNull(message);

		// The per-account/IMAP lease is held from just before connect until the
		// disconnect/dispose cleanup completes; the gate queues callers instead of
		// failing them.
		IAsyncDisposable lease = await connectionGate
			.AcquireAsync(profile.Id, "imap", cancellationToken)
			.ConfigureAwait(false);
		ImapClient? client = null;
		try
		{
			try
			{
				client = await clientFactory.CreateAsync(profile, credential, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (MailOperationException exception)
			{
				// Connection or authentication failure before any append activity.
				return SendTransportOutcome.Failed(exception.Error);
			}
			catch (Exception exception)
			{
				return SendTransportOutcome.Failed(
					ProtocolExceptionMapper.Map(exception, "imap", "drafts_save", cancellationToken).Error);
			}

			return await AppendAsync(client, message, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			if (client is not null)
				await DisconnectAndDisposeAsync(client).ConfigureAwait(false);
			await lease.DisposeAsync().ConfigureAwait(false);
		}
	}

	private async Task<SendTransportOutcome> AppendAsync(
		ImapClient client,
		PreparedOutgoingMessage message,
		CancellationToken cancellationToken)
	{
		IMailFolder folder;
		try
		{
			folder = await ResolveDraftsFolderAsync(client, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (MailOperationException exception)
		{
			return SendTransportOutcome.Failed(exception.Error);
		}
		catch (Exception exception)
		{
			// Nothing has been appended yet, so resolution failures are definitive.
			return SendTransportOutcome.Failed(
				ProtocolExceptionMapper.Map(exception, "imap", "drafts_save", cancellationToken).Error);
		}

		MimeMessage mime;
		try
		{
			using var buffer = new MemoryStream(message.MimeMessage, writable: false);
			mime = await MimeMessage.LoadAsync(buffer, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return SendTransportOutcome.Failed(MessageInvalid());
		}

		// Defense-in-depth: the prepared MIME must never carry Bcc headers; drafts
		// show To/Cc only, exactly as composed.
		mime.Bcc.Clear();

		bool appendStarted = false;
		try
		{
			using var scope = CommandTimeoutScope.Create(commandTimeout, cancellationToken);
			appendStarted = true;
			await folder.AppendAsync(
				FormatOptions.Default,
				new AppendRequest(mime, MessageFlags.Draft, message.Preview.PreparedAt),
				scope.Token).ConfigureAwait(false);
			return SendTransportOutcome.Succeeded();
		}
		catch (ImapCommandException)
		{
			// The server definitively rejected the APPEND; nothing was saved.
			return SendTransportOutcome.Failed(Rejected());
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (!appendStarted)
		{
			return SendTransportOutcome.Failed(
				ProtocolExceptionMapper.Map(exception, "imap", "drafts_save", cancellationToken).Error);
		}
		catch (OperationCanceledException)
		{
			// Command timeout after the APPEND began: the outcome is unknown.
			return SendTransportOutcome.Indeterminate(TransportUnknown());
		}
		catch (Exception)
		{
			// Any other transport failure after the APPEND began (I/O error,
			// protocol disconnect, and so on) is ambiguous for the server side; the
			// store never reconnects and re-appends.
			return SendTransportOutcome.Indeterminate(TransportUnknown());
		}
	}

	private async Task<IMailFolder> ResolveDraftsFolderAsync(
		ImapClient client,
		CancellationToken cancellationToken)
	{
		if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
		{
			IMailFolder? special = client.GetFolder(SpecialFolder.Drafts);
			if (special is not null)
				return special;
		}

		if (client.PersonalNamespaces.Count == 0)
			throw FolderNotFound();

		foreach (FolderNamespace ns in client.PersonalNamespaces)
		{
			IList<IMailFolder> folders;
			using (var scope = CommandTimeoutScope.Create(commandTimeout, cancellationToken))
			{
				folders = await client.GetFoldersAsync(
					ns, StatusItems.None, subscribedOnly: false, scope.Token).ConfigureAwait(false);
			}

			IMailFolder? match = folders.FirstOrDefault(folder =>
				IsDraftsName(folder.Name) || IsDraftsName(folder.FullName));
			if (match is not null)
				return match;
		}

		throw FolderNotFound();
	}

	private static bool IsDraftsName(string? name) =>
		string.Equals(name, "Drafts", StringComparison.OrdinalIgnoreCase) ||
		string.Equals(name, "草稿箱", StringComparison.OrdinalIgnoreCase);

	private async Task DisconnectAndDisposeAsync(ImapClient client)
	{
		try
		{
			if (client.IsConnected)
			{
				using var scope = CommandTimeoutScope.Create(commandTimeout, CancellationToken.None);
				await client.DisconnectAsync(true, scope.Token).ConfigureAwait(false);
			}
		}
		catch
		{
			// Cleanup cannot replace the stable result of the requested operation.
		}
		finally
		{
			client.Dispose();
		}
	}

	private static MailOperationException FolderNotFound() => new(new ToolError(
		"drafts.folder_not_found",
		ErrorCategory.Capability,
		"The account has no Drafts folder to save the message into.",
		false,
		null,
		new Dictionary<string, string> { ["protocol"] = "imap" }));

	private static ToolError Rejected() => new(
		"drafts.append_rejected",
		ErrorCategory.Transient,
		"The IMAP server rejected the Drafts-folder append.",
		false,
		null,
		new Dictionary<string, string> { ["protocol"] = "imap" });

	private static ToolError MessageInvalid() => new(
		"drafts.message_invalid",
		ErrorCategory.Validation,
		"The prepared message could not be parsed for the Drafts folder.",
		false,
		null,
		new Dictionary<string, string> { ["protocol"] = "imap" });

	private static ToolError TransportUnknown() => new(
		"drafts.transport_unknown",
		ErrorCategory.Transient,
		"The IMAP transport failed after the append began; the server may still have saved the draft.",
		false,
		null,
		new Dictionary<string, string> { ["protocol"] = "imap" });
}
