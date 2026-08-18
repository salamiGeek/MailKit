using System.Text.Json;
using MailKit.Agent.Core.Errors;
using MailKit.Agent.Core.Policy;

namespace MailKit.Agent.Core.Tests.Policy;

public class OperationPolicyTests
{
    [TestCase(RiskLevel.ReadOnly, false)]
    [TestCase(RiskLevel.RecoverableWrite, false)]
    [TestCase(RiskLevel.ExternalOrIrreversible, true)]
    public void ConfirmationMatchesRisk(RiskLevel risk, bool expected)
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_operation", risk, 1, 1024));

        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.True);
            Assert.That(decision.ConfirmationRequired, Is.EqualTo(expected));
            Assert.That(decision.Error, Is.Null);
        });
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void RejectsNonPositiveItemCounts(int itemCount)
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_search", RiskLevel.ReadOnly, itemCount, 1024));

        AssertDenied(decision, "policy.invalid_count");
    }

    [Test]
    public void AllowsRequestsAtHardBatchLimit()
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_search", RiskLevel.ReadOnly, 500, 1024));

        Assert.That(decision.Allowed, Is.True);
    }

    [Test]
    public void RejectsRequestsOverHardBatchLimit()
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_search", RiskLevel.ReadOnly, 501, 1024));

        AssertDenied(decision, "policy.batch_limit_exceeded");
    }

    [Test]
    public void RejectsNegativeOutputEstimates()
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_search", RiskLevel.ReadOnly, 1, -1));

        AssertDenied(decision, "policy.output_limit_exceeded");
    }

    [Test]
    public void AllowsOutputAtHardByteLimit()
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_search", RiskLevel.ReadOnly, 1, 1_048_576));

        Assert.That(decision.Allowed, Is.True);
    }

    [Test]
    public void RejectsOutputOverHardByteLimit()
    {
        var decision = OperationPolicy.Default.Evaluate(
            new("message_search", RiskLevel.ReadOnly, 1, 1_048_577));

        AssertDenied(decision, "policy.output_limit_exceeded");
    }

    [Test]
    public void RiskLevelUsesLowerSnakeCaseJson()
    {
        var json = JsonSerializer.Serialize(RiskLevel.ExternalOrIrreversible);

        Assert.That(json, Is.EqualTo("\"external_or_irreversible\""));
    }

    [Test]
    public void OperationDescriptorUsesStableSnakeCaseJsonFields()
    {
        var descriptor = new OperationDescriptor(
            "message_search", RiskLevel.RecoverableWrite, 2, 2048);

        var json = JsonSerializer.Serialize(descriptor);

        Assert.That(json, Is.EqualTo(
            "{\"name\":\"message_search\",\"risk\":\"recoverable_write\",\"item_count\":2,\"estimated_output_bytes\":2048}"));
    }

    [Test]
    public void PolicyLimitsUsesStableSnakeCaseJsonFields()
    {
        var limits = new PolicyLimits(500, 1_048_576);

        var json = JsonSerializer.Serialize(limits);

        Assert.That(json, Is.EqualTo(
            "{\"max_batch_items\":500,\"max_structured_output_bytes\":1048576}"));
    }

    [Test]
    public void PolicyDecisionUsesStableSnakeCaseJsonFields()
    {
        var decision = new PolicyDecision(true, true, null);

        var json = JsonSerializer.Serialize(decision);

        Assert.That(json, Is.EqualTo(
            "{\"allowed\":true,\"confirmation_required\":true,\"error\":null}"));
    }

    [Test]
    public void ConstructorRejectsNullLimits()
    {
        Assert.Throws<ArgumentNullException>(() => new OperationPolicy(null!));
    }

    private static void AssertDenied(PolicyDecision decision, string expectedCode)
    {
        Assert.Multiple(() =>
        {
            Assert.That(decision.Allowed, Is.False);
            Assert.That(decision.ConfirmationRequired, Is.False);
            Assert.That(decision.Error, Is.Not.Null);
            Assert.That(decision.Error!.Code, Is.EqualTo(expectedCode));
            Assert.That(decision.Error.Category, Is.EqualTo(ErrorCategory.Policy));
            Assert.That(decision.Error.Retryable, Is.False);
            Assert.That(decision.Error.RetryAfter, Is.Null);
            Assert.That(decision.Error.Details, Is.Null);
        });
    }
}
