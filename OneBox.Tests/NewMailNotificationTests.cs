using MailKit;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class NewMailNotificationTests
{
    [TestMethod]
    public void InboxAndJunkEachUseTheirOwnUidAndNeverRepeatStoredMail()
    {
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cursor = new SyncCursor(accountId, "INBOX", null, 10, 100, now);
        var junkCursor = new SyncCursor(accountId, "JUNK", null, 20, 500, now);
        var inbox = Message(accountId, "101", now.AddMinutes(-1));
        var junk = Message(accountId, new ImapMessageLocation("Spam", 20, new UniqueId(501)).ServerId, now);
        var oldJunk = junk with { ServerId = new ImapMessageLocation("Spam", 20, new UniqueId(499)).ServerId };
        var messageList = new[] { inbox, junk, inbox, oldJunk, inbox with { ServerId = "99" },
            inbox with { ServerId = "102", IsRead = true }, inbox with { ServerId = "103" },
            inbox with { ServerId = "104", AccountId = Guid.NewGuid() } };
        var existingIdSet = new HashSet<string> { "103" };
        var notificationList = NewMailNotifications.Select(messageList, cursor, 10, existingIdSet,
            junkCursor, junkCursor with { HighestUid = 501 });
        CollectionAssert.AreEqual(new[] { inbox.Key, junk.Key }, notificationList.Select(message => message.Key).ToArray());
        existingIdSet.UnionWith(notificationList.Select(message => message.ServerId));
        Assert.AreEqual(0, NewMailNotifications.Select(messageList, cursor, 10, existingIdSet,
            junkCursor, junkCursor with { HighestUid = 501 }).Count);
    }

    [TestMethod]
    public void FirstSyncAndFolderValidityResetDoNotReplayHistoricalMail()
    {
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var message = Message(accountId, "101", now);
        var cursor = new SyncCursor(accountId, "INBOX", null, 10, 100, now);
        var existingIdSet = new HashSet<string>();
        Assert.AreEqual(0, NewMailNotifications.Select([message], null, 10, existingIdSet).Count);
        Assert.AreEqual(0, NewMailNotifications.Select([message], cursor with { LastSuccess = null }, 10, existingIdSet).Count);
        Assert.AreEqual(0, NewMailNotifications.Select([message], cursor, 11, existingIdSet).Count);
        var junk = message with { ServerId = new ImapMessageLocation("Spam", 21, new UniqueId(501)).ServerId };
        var junkCursor = new SyncCursor(accountId, "JUNK", null, 20, 500, now);
        Assert.AreEqual(0, NewMailNotifications.Select([junk], cursor, 10, existingIdSet).Count);
        Assert.AreEqual(0, NewMailNotifications.Select([junk], cursor, 10, existingIdSet,
            junkCursor, junkCursor with { UidValidity = 21 }).Count);
    }

    [TestMethod]
    public void GmailApiKeepsNewUnreadMailIncludingSpamAndSkipsBackfill()
    {
        var accountId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cursor = new SyncCursor(accountId, "INBOX", "old-history", null, 0, now);
        var message = Message(accountId, "a1b2", now.AddSeconds(1));
        var spam = message with { ServerId = "c3d4" };
        var notificationList = NewMailNotifications.Select([message, spam,
            message with { ServerId = "old", ReceivedAt = now.AddDays(-2) },
            message with { ServerId = "read", IsRead = true },
            message with { ServerId = "trash", FolderId = "TRASH" }], cursor, null, new HashSet<string>());
        CollectionAssert.AreEquivalent(new[] { "a1b2", "c3d4" }, notificationList.Select(item => item.ServerId).ToArray());
    }

    [TestMethod]
    public async Task StoredIdBaselineIncludesMoreThanListLimitAndStaysWithinAccount()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var account = new Account(Guid.NewGuid(), "example@qq.com", "测试", ProviderKind.Qq);
        var otherAccount = account with { Id = Guid.NewGuid() };
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            await store.UpsertAccountAsync(otherAccount);
            await store.UpsertMessagesAsync(Enumerable.Range(1, 1002).Select(index => Message(account.Id, index.ToString(), DateTimeOffset.UtcNow)));
            await store.UpsertMessagesAsync([Message(otherAccount.Id, "other", DateTimeOffset.UtcNow)]);
            var messageIdSet = await store.GetMessageIdSetAsync(account.Id);
            Assert.AreEqual(1002, messageIdSet.Count);
            Assert.IsTrue(messageIdSet.Contains("1"));
            Assert.IsFalse(messageIdSet.Contains("other"));
            Assert.AreEqual(1003, await store.GetUnreadCountAsync(), "角标应统计所有账号，不受列表数量限制");
            var message = Message(account.Id, "1", DateTimeOffset.UtcNow);
            await store.UpsertMessagesAsync([message with { IsRead = true },
                message with { ServerId = "trash", FolderId = "TRASH" }]);
            Assert.AreEqual(1002, await store.GetUnreadCountAsync(), "已读邮件和废纸篓不计入角标");
            await store.UpsertMessagesAsync([message]);
            Assert.AreEqual(1003, await store.GetUnreadCountAsync(), "标为未读后应重新计数");
            var junk = message with { ServerId = new ImapMessageLocation("Spam", 20, new UniqueId(501)).ServerId };
            await store.UpsertMessagesAsync([junk]);
            Assert.AreEqual(1004, await store.GetUnreadCountAsync(), "垃圾邮件也应计入角标");
            await store.RemoveAccountAsync(account.Id);
            Assert.AreEqual(1, await store.GetUnreadCountAsync(), "移除账号后仅保留其他账号的未读数");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void ExistingSettingsDefaultToQuietNotificationsAndChoicesSurviveReload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"OneBox-notification-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"FontScalePercent\":115}");
            var settings = ReaderSettings.Load(path);
            Assert.IsTrue(settings.Notifications.Enabled);
            Assert.AreEqual(NotificationCorner.BottomRight, settings.Notifications.Corner);
            Assert.AreEqual(5, settings.Notifications.DurationSeconds);
            Assert.AreEqual(3, settings.Notifications.MaxVisible);
            var notifications = new DesktopNotificationSettings
            {
                Enabled = false, Corner = NotificationCorner.TopLeft, DurationSeconds = 10,
                MaxVisible = 1, ShowContent = false, ScreenDeviceName = "test-display"
            };
            (settings with { Notifications = notifications }).Save(path);
            Assert.AreEqual(notifications, ReaderSettings.Load(path).Notifications);
            Assert.AreEqual(115, ReaderSettings.Load(path).FontScalePercent);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static MailMessage Message(Guid accountId, string serverId, DateTimeOffset receivedAt) =>
        new(accountId, serverId, "INBOX", "新邮件", "发件人", "sender@example.com", "", receivedAt, "摘要", false, false, false);
}
