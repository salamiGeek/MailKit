using System.Text.Json;
using MailKit.Agent.Core.Errors;

namespace MailKit.Agent.Core.Tests.Errors;

public class ToolResultTests
{
    [Test]
    public void FailureUsesStableSnakeCaseShape()
    {
        var result = ToolResult<string>.Failure(
            new ToolError("account.not_found", ErrorCategory.Validation,
                "Account was not found.", false, null, null),
            "corr-123");

        var json = JsonSerializer.Serialize(result);

        Assert.That(json, Does.Contain("\"correlation_id\":\"corr-123\""));
        Assert.That(json, Does.Contain("\"category\":\"validation\""));
        Assert.That(json, Does.Contain("\"retryable\":false"));
        Assert.That(json, Does.Not.Contain("password").IgnoreCase);
    }

    [Test]
    public void SuccessCannotContainAnError()
    {
        var result = ToolResult<int>.Success(42, "corr-456");

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Data, Is.EqualTo(42));
            Assert.That(result.Error, Is.Null);
        });
    }
}
