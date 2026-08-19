namespace MailKit.Agent.Core.Errors;

public sealed class MailOperationException : Exception
{
    public MailOperationException(ToolError error)
        : base(RequireError(error).Message)
    {
        Error = error;
    }

    public ToolError Error { get; }

    private static ToolError RequireError(ToolError error) =>
        error ?? throw new ArgumentNullException(nameof(error));
}
