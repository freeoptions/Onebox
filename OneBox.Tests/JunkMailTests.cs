using MailKit;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class JunkMailTests
{
    [TestMethod]
    public void JunkUidDoesNotCollideWithInboxAndRetainsFolderValidity()
    {
        var inbox = ImapMessageLocation.Parse("42");
        var junk = new ImapMessageLocation("[Gmail]/垃圾邮件", 123, new UniqueId(42));
        Assert.AreEqual("42", inbox.ServerId);
        Assert.AreNotEqual(inbox.ServerId, junk.ServerId);
        Assert.AreEqual(junk, ImapMessageLocation.Parse(junk.ServerId));
        Assert.AreNotEqual(junk.ServerId, (junk with { UidValidity = 124 }).ServerId);
        Assert.IsFalse(long.TryParse(junk.ServerId, out _), "垃圾邮件不能污染收件箱最高UID游标");
    }

    [TestMethod]
    public async Task MovedJunkMailReplacesOldInboxEntryWithoutDeletingOtherAccountsOrOldMail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var account = new Account(Guid.NewGuid(), "example@qq.com", "测试", ProviderKind.Qq);
        var other = account with { Id = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;
        var inboxMessage = new MailMessage(account.Id, "42", "INBOX", "测试", "", "", "", now, "", false, false, false);
        var junkMessage = inboxMessage with { ServerId = new ImapMessageLocation("Junk", 10, new UniqueId(42)).ServerId };
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            await store.UpsertAccountAsync(other);
            await store.UpsertMessagesAsync([inboxMessage, junkMessage, inboxMessage with { AccountId = other.Id },
                inboxMessage with { ServerId = "40", ReceivedAt = now.AddDays(-100) }]);
            await store.EnqueueAsync(new PendingAction(Guid.NewGuid(), account.Id, "42", PendingActionKind.MarkRead, now));
            await store.RemoveMissingMessagesAsync(account.Id, "INBOX", new HashSet<string> { junkMessage.ServerId }, now.AddDays(-90));
            var messageList = await store.GetMessagesAsync(account.Id, "INBOX", null);
            Assert.AreEqual(2, messageList.Count);
            Assert.IsTrue(messageList.Any(message => message.ServerId == junkMessage.ServerId));
            Assert.IsTrue(messageList.Any(message => message.ServerId == "40"));
            Assert.AreEqual(1, (await store.GetMessagesAsync(other.Id, "INBOX", null)).Count);
            Assert.AreEqual(0, (await store.GetPendingAsync(account.Id)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
