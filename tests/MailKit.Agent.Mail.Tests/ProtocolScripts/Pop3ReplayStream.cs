using System.Text;

namespace MailKit.Agent.Mail.Tests.ProtocolScripts;

internal sealed class Pop3ReplayCommand
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    public Pop3ReplayCommand(string command, string response)
    {
        Command = command;
        CommandBuffer = Latin1.GetBytes(command);
        Response = Latin1.GetBytes(response);
    }

    public string Command { get; }

    public byte[] CommandBuffer { get; }

    public byte[] Response { get; }

    public Encoding Encoding => Latin1;
}

internal sealed class Pop3ReplayStream : Stream
{
    private enum ReplayState
    {
        SendResponse,
        WaitForCommand
    }

    private readonly MemoryStream sent = new();
    private readonly IReadOnlyList<Pop3ReplayCommand> commands;
    private MemoryStream response;
    private ReplayState state = ReplayState.SendResponse;
    private int index;
    private bool disposed;
    private string? unexpectedCommand;

    public Pop3ReplayStream(IReadOnlyList<Pop3ReplayCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
            throw new ArgumentException("At least one replay step is required.", nameof(commands));

        this.commands = commands;
        response = OpenResponse(commands[0]);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override bool CanTimeout => true;

    public override long Length => response.Length;

    public override long Position
    {
        get => response.Position;
        set => throw new NotSupportedException();
    }

    public override int ReadTimeout { get; set; } = 100_000;

    public override int WriteTimeout { get; set; } = 100_000;

    public void AssertComplete()
    {
        Assert.That(unexpectedCommand, Is.Null, unexpectedCommand);
        Assert.That(index, Is.EqualTo(commands.Count),
            $"Replay stopped before command {index}: {NextCommandDescription()}");
        Assert.That(state, Is.EqualTo(ReplayState.WaitForCommand));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureResponseReady();

        int read = response.Read(buffer, offset, count);
        if (response.Position == response.Length && state == ReplayState.SendResponse)
        {
            state = ReplayState.WaitForCommand;
            index++;
        }

        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override int Read(Span<byte> buffer)
    {
        byte[] temporary = new byte[buffer.Length];
        int read = Read(temporary, 0, temporary.Length);
        temporary.AsSpan(0, read).CopyTo(buffer);
        return read;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] temporary = new byte[buffer.Length];
        int read = Read(temporary, 0, temporary.Length);
        temporary.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Assert.That(state, Is.EqualTo(ReplayState.WaitForCommand),
            "A command was written before the previous response was consumed.");
        if (index >= commands.Count)
        {
            sent.Write(buffer, offset, count);
            unexpectedCommand = "The client emitted an unexpected command after the replay script ended: " +
                Encoding.ASCII.GetString(sent.GetBuffer(), 0, checked((int)sent.Length));
            Assert.Fail(unexpectedCommand);
        }

        sent.Write(buffer, offset, count);
        Pop3ReplayCommand expected = commands[index];
        if (sent.Length < expected.CommandBuffer.Length)
            return;

        string actual = expected.Encoding.GetString(sent.GetBuffer(), 0, checked((int)sent.Length));
        Assert.That(actual, Is.EqualTo(expected.Command), "Commands did not match.");

        response.Dispose();
        response = OpenResponse(expected);
        state = ReplayState.SendResponse;
        sent.SetLength(0);
    }

    public override Task WriteAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] temporary = buffer.ToArray();
        Write(temporary, 0, temporary.Length);
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
            response.Dispose();
            sent.Dispose();
            disposed = true;
        }

        base.Dispose(disposing);
    }

    private static MemoryStream OpenResponse(Pop3ReplayCommand command) =>
        new(command.Response, writable: false);

    private void EnsureResponseReady()
    {
        if (state == ReplayState.SendResponse || index >= commands.Count)
            return;

        string actual = commands[index].Encoding.GetString(
            sent.GetBuffer(), 0, checked((int)sent.Length));
        Assert.Fail($"Client attempted to read before sending the next command. Sent: {actual}");
    }

    private string NextCommandDescription() =>
        index < commands.Count ? commands[index].Command : "<none>";
}
