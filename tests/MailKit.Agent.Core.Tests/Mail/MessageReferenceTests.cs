using System.Text.Json;
using MailKit.Agent.Core.Mail;

namespace MailKit.Agent.Core.Tests.Mail;

public class MessageReferenceTests
{
    [Test]
    public void ImapReferenceRequiresUidValidity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MessageReference.ForImap("acct", "INBOX", 0, 12));
    }

    [Test]
    public void ImapReferenceRequiresUid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MessageReference.ForImap("acct", "INBOX", 42, 0));
    }

    [TestCase(null, "INBOX")]
    [TestCase("", "INBOX")]
    [TestCase(" ", "INBOX")]
    [TestCase("acct", null)]
    [TestCase("acct", "")]
    [TestCase("acct", " ")]
    public void ImapReferenceRequiresAccountAndFolder(string? accountId, string? folderId)
    {
        Assert.That(
            () => MessageReference.ForImap(accountId!, folderId!, 42, 12),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void ImapReferenceContainsStableIdentity()
    {
        var reference = MessageReference.ForImap("acct", "INBOX", 42, 12);

        Assert.Multiple(() =>
        {
            Assert.That(reference.Protocol, Is.EqualTo(MailProtocol.Imap));
            Assert.That(reference.AccountId, Is.EqualTo("acct"));
            Assert.That(reference.FolderId, Is.EqualTo("INBOX"));
            Assert.That(reference.UidValidity, Is.EqualTo(42));
            Assert.That(reference.Uid, Is.EqualTo(12));
            Assert.That(reference.Uidl, Is.Null);
        });
    }

    [TestCase(null, "uidl-1")]
    [TestCase("", "uidl-1")]
    [TestCase(" ", "uidl-1")]
    [TestCase("acct", null)]
    [TestCase("acct", "")]
    [TestCase("acct", " ")]
    public void Pop3ReferenceRequiresAccountAndUidl(string? accountId, string? uidl)
    {
        Assert.That(
            () => MessageReference.ForPop3(accountId!, uidl!),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Pop3ReferenceContainsStableIdentity()
    {
        var reference = MessageReference.ForPop3("acct", "uidl-1");

        Assert.Multiple(() =>
        {
            Assert.That(reference.Protocol, Is.EqualTo(MailProtocol.Pop3));
            Assert.That(reference.AccountId, Is.EqualTo("acct"));
            Assert.That(reference.FolderId, Is.Null);
            Assert.That(reference.UidValidity, Is.Null);
            Assert.That(reference.Uid, Is.Null);
            Assert.That(reference.Uidl, Is.EqualTo("uidl-1"));
        });
    }

    [TestCase(MailProtocol.Imap, "imap")]
    [TestCase(MailProtocol.Pop3, "pop3")]
    public void ProtocolUsesStableLowerSnakeCaseJson(MailProtocol protocol, string expected)
    {
        var json = JsonSerializer.Serialize(protocol);

        Assert.That(json, Is.EqualTo($"\"{expected}\""));
    }
}
