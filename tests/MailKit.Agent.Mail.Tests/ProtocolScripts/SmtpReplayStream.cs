using System.Text;

namespace MailKit.Agent.Mail.Tests.ProtocolScripts;

/// <summary>
/// The state machine of <see cref="SmtpReplayStream"/> after a response has been
/// consumed. <see cref="WaitForEndOfData"/> applies to the step that follows a
/// DATA command; <see cref="UnexpectedDisconnect"/> models a server that closes
/// the connection (reads return EOF and further writes are swallowed).
/// </summary>
internal enum SmtpReplayState
{
    SendResponse,
    WaitForCommand,
    WaitForEndOfData,
    UnexpectedDisconnect
}

/// <summary>
/// One scripted SMTP exchange step: the exact command the client must write and
/// the canned response the server replays. Commands are matched as UTF-8 bytes so
/// SMTPUTF8 exchanges replay correctly.
/// </summary>
internal sealed class SmtpReplayCommand
{
    private static readonly Encoding ProtocolEncoding = Encoding.UTF8;

    public SmtpReplayCommand(string command, string response)
        : this(command, response, InferNextState(command))
    {
    }

    public SmtpReplayCommand(string command, string response, SmtpReplayState nextState)
    {
        Command = command;
        CommandBuffer = ProtocolEncoding.GetBytes(command);
        Response = ProtocolEncoding.GetBytes(response);
        NextState = nextState;
    }

    public string Command { get; }

    public byte[] CommandBuffer { get; }

    public byte[] Response { get; }

    public SmtpReplayState NextState { get; }

    /// <summary>
    /// When set, the server never produces the response for this command; reads
    /// stall until the caller's cancellation token fires (timeout coverage).
    /// </summary>
    public bool NeverRespond { get; init; }

    public Encoding Encoding => ProtocolEncoding;

    private static SmtpReplayState InferNextState(string command) =>
        command == "DATA\r\n" ? SmtpReplayState.WaitForEndOfData : SmtpReplayState.WaitForCommand;
}

/// <summary>
/// A deterministic in-memory SMTP server driven by a replay script, adapted from
/// the upstream MailKit UnitTests SmtpReplayStream. The step following a DATA
/// command uses <c>".\r\n"</c> as its expected command so the end-of-data marker
/// completes the exchange. DATA payloads are captured so tests can assert that
/// the transmitted message body never contains Bcc headers.
/// </summary>
internal sealed class SmtpReplayStream : Stream
{
    private static readonly byte[] EndOfData = "\r\n.\r\n"u8.ToArray();

    private readonly MemoryStream sent = new();
    private readonly MemoryStream dataPayload = new();
    private readonly IReadOnlyList<SmtpReplayCommand> commands;
    private readonly List<byte[]> dataPayloads = [];
    private SmtpReplayCommand? pending;
    private SmtpReplayState state = SmtpReplayState.SendResponse;
    private MemoryStream? response;
    private int index;
    private bool disposed;
    private string? unexpectedCommand;

    public SmtpReplayStream(IReadOnlyList<SmtpReplayCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("At least one replay step is required.", nameof(commands));

        this.commands = commands;
        OpenResponse(commands[0]);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override bool CanTimeout => true;

    public override long Length => response?.Length ?? 0;

    public override long Position
    {
        get => response?.Position ?? 0;
        set => throw new NotSupportedException();
    }

    public override int ReadTimeout { get; set; } = 100_000;

    public override int WriteTimeout { get; set; } = 100_000;

    /// <summary>The DATA payloads written by the client, without the end-of-data marker.</summary>
    public IReadOnlyList<byte[]> DataPayloads => dataPayloads;

    /// <summary>The number of DATA payloads the client transmitted.</summary>
    public int DataCommandCount => dataPayloads.Count;

    public void AssertComplete()
    {
        Assert.That(unexpectedCommand, Is.Null, unexpectedCommand);
        bool stalledAtNeverRespondingStep =
            pending is { NeverRespond: true } && index == commands.Count - 1;
        Assert.That(index == commands.Count || stalledAtNeverRespondingStep,
            $"Replay stopped before command {index}: {NextCommandDescription()}");
        if (stalledAtNeverRespondingStep)
            return;

        Assert.That(state, Is.EqualTo(SmtpReplayState.WaitForCommand)
                .Or.EqualTo(SmtpReplayState.UnexpectedDisconnect),
            $"Replay ended in state {state}.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pending is { NeverRespond: true })
            throw new NotSupportedException("This step never responds; use the async read path.");

        if (state == SmtpReplayState.UnexpectedDisconnect)
            return 0;

        EnsureResponseReady();
        MemoryStream current = response!;
        int read = current.Read(buffer, offset, count);
        if (current.Position == current.Length)
            ConsumeResponse();

        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (pending is { NeverRespond: true })
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

        return Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] temporary = new byte[buffer.Length];
        int read = await ReadAsync(temporary, 0, temporary.Length, cancellationToken)
            .ConfigureAwait(false);
        temporary.AsMemory(0, read).CopyTo(buffer);
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (state == SmtpReplayState.UnexpectedDisconnect || pending is { NeverRespond: true })
        {
            // The server never responds (or has closed the connection); the
            // client's disconnect-time writes go into the void.
            return;
        }

        Assert.That(state, Is.EqualTo(SmtpReplayState.WaitForCommand)
                .Or.EqualTo(SmtpReplayState.WaitForEndOfData),
            "A command was written before the previous response was consumed.");

        if (index >= commands.Count)
        {
            sent.Write(buffer, offset, count);
            unexpectedCommand = "The client emitted an unexpected command after the replay script ended: " +
                Encoding.ASCII.GetString(sent.GetBuffer(), 0, checked((int)sent.Length));
            Assert.Fail(unexpectedCommand);
        }

        if (state == SmtpReplayState.WaitForEndOfData)
        {
            WriteDataPayload(buffer, offset, count);
            return;
        }

        sent.Write(buffer, offset, count);
        SmtpReplayCommand expected = commands[index];
        if (sent.Length < expected.CommandBuffer.Length)
            return;

        string actual = expected.Encoding.GetString(sent.GetBuffer(), 0, checked((int)sent.Length));
        Assert.That(actual, Is.EqualTo(expected.Command), "Commands did not match.");

        ServeResponse(expected);
        sent.SetLength(0);
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] temporary = buffer.ToArray();
        Write(temporary, 0, temporary.Length);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            response?.Dispose();
            sent.Dispose();
            dataPayload.Dispose();
            disposed = true;
        }

        base.Dispose(disposing);
    }

    private void WriteDataPayload(byte[] buffer, int offset, int count)
    {
        dataPayload.Write(buffer, offset, count);

        byte[] written = dataPayload.GetBuffer();
        long length = dataPayload.Length;
        bool endOfDataReached =
            length >= EndOfData.Length &&
            EndOfData.AsSpan().SequenceEqual(
                written.AsSpan(checked((int)length - EndOfData.Length), EndOfData.Length));

        if (!endOfDataReached)
            return;

        SmtpReplayCommand expected = commands[index];
        Assert.That(expected.Command, Is.EqualTo(".\r\n"),
            "The step after DATA must expect the end-of-data marker.");

        int payloadLength = checked((int)length - EndOfData.Length);
        byte[] payload = new byte[payloadLength];
        Array.Copy(written, payload, payloadLength);
        dataPayloads.Add(payload);
        dataPayload.SetLength(0);

        ServeResponse(expected);
        sent.SetLength(0);
    }

    private void ServeResponse(SmtpReplayCommand command)
    {
        response?.Dispose();
        response = null;
        pending = command;
        if (!command.NeverRespond)
            OpenResponse(command);
        state = SmtpReplayState.SendResponse;
    }

    private void OpenResponse(SmtpReplayCommand command)
    {
        response?.Dispose();
        response = new MemoryStream(command.Response, writable: false);
        pending = command;
    }

    private void ConsumeResponse()
    {
        SmtpReplayCommand current = commands[index];
        state = current.NextState;
        index++;
        response!.Dispose();
        response = null;
        pending = null;
    }

    private void EnsureResponseReady()
    {
        if (state == SmtpReplayState.SendResponse)
            return;

        if (index >= commands.Count)
            return;

        string actual = commands[index].Encoding.GetString(
            sent.GetBuffer(), 0, checked((int)sent.Length));
        Assert.Fail($"Client attempted to read before sending the next command. Sent: {actual}");
    }

    private string NextCommandDescription() =>
        index < commands.Count ? commands[index].Command : "<none>";
}
