using System.Text.Json;
using MailKit.Agent.Core.Accounts;

namespace MailKit.Agent.Core.Tests.Serialization;

public class LowerSnakeCaseEnumConverterTests
{
    [Test]
    public void RoundTripsDefinedValuesAsLowerSnakeCaseStrings()
    {
        var json = JsonSerializer.Serialize(AuthenticationKind.OAuth2);
        var value = JsonSerializer.Deserialize<AuthenticationKind>("\"o_auth2\"");

        Assert.Multiple(() =>
        {
            Assert.That(json, Is.EqualTo("\"o_auth2\""));
            Assert.That(value, Is.EqualTo(AuthenticationKind.OAuth2));
        });
    }

    [Test]
    public void RoundTripsSendModeValuesAsLowerSnakeCaseStrings()
    {
        var confirmJson = JsonSerializer.Serialize(SendMode.ConfirmDialog);
        var draftsJson = JsonSerializer.Serialize(SendMode.Drafts);

        Assert.Multiple(() =>
        {
            Assert.That(confirmJson, Is.EqualTo("\"confirm_dialog\""));
            Assert.That(draftsJson, Is.EqualTo("\"drafts\""));
            Assert.That(JsonSerializer.Deserialize<SendMode>("\"confirm_dialog\""),
                Is.EqualTo(SendMode.ConfirmDialog));
            Assert.That(JsonSerializer.Deserialize<SendMode>("\"drafts\""),
                Is.EqualTo(SendMode.Drafts));
        });
    }

    [Test]
    public void RejectsNumericJsonValues()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AuthenticationKind>("999"));
    }

    [Test]
    public void RejectsSerializingUndefinedValues()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Serialize((AuthenticationKind)999));
    }
}
