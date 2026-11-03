using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.Sqlite;
using OneBox.Core;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class MailRulesTests
{
    [TestMethod]
    public void UnifiedInboxKeepsSameServerIdFromDifferentAccounts()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        var messages = new[]
        {
            new MailMessage(first, "42", "INBOX", "同一主题", "甲", "a@example.com", "", at, "", false, false, false),
            new MailMessage(second, "42", "INBOX", "同一主题", "甲", "a@example.com", "", at, "", false, false, false),
            new MailMessage(first, "42", "INBOX", "同一主题", "甲", "a@example.com", "", at.AddMinutes(-1), "", false, false, false)
        };

        var result = MailRules.UnifiedInbox(messages);

        Assert.AreEqual(2, result.Count);
        CollectionAssert.AreEquivalent(new[] { first, second }, result.Select(message => message.AccountId).ToArray());
    }

    [TestMethod]
    public void AttachmentNameCannotEscapeSaveDirectoryOrOverwriteExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "report.pdf"), "keep");
            var path = MailRules.AvailableAttachmentPath(directory, "../../report.pdf");
            Assert.AreEqual(Path.Combine(directory, "report (1).pdf"), path);
            Assert.AreEqual("keep", File.ReadAllText(Path.Combine(directory, "report.pdf")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task RemovingOneAccountPreservesOtherAccountsMail()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var one = new Account(Guid.NewGuid(), "one@qq.com", "甲", ProviderKind.Qq);
        var two = new Account(Guid.NewGuid(), "two@163.com", "乙", ProviderKind.NetEase);
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(one);
            await store.UpsertAccountAsync(two);
            await store.UpsertMessagesAsync(new[]
            {
                new MailMessage(one.Id, "1", "INBOX", "A", "甲", "a@example.com", "", DateTimeOffset.UtcNow, "", false, false, false),
                new MailMessage(two.Id, "1", "INBOX", "B", "乙", "b@example.com", "", DateTimeOffset.UtcNow, "", false, false, false)
            });
            await store.RemoveAccountAsync(one.Id);
            Assert.AreEqual(1, (await store.GetMessagesAsync(null, "INBOX", null)).Count);
            Assert.AreEqual(two.Id, (await store.GetMessagesAsync(null, "INBOX", null))[0].AccountId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SearchSupportsChineseFullPinyinAndInitials()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var account = new Account(Guid.NewGuid(), "one@qq.com", "甲", ProviderKind.Qq);
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            await store.UpsertMessagesAsync([
                new MailMessage(account.Id, "1", "INBOX", "北京会议", "李明", "a@example.com",
                    "", DateTimeOffset.UtcNow, "", false, false, false)
            ]);
            Assert.AreEqual(1, (await store.GetMessagesAsync(null, "INBOX", "北京")).Count);
            Assert.AreEqual(1, (await store.GetMessagesAsync(null, "INBOX", "beijing")).Count);
            Assert.AreEqual(1, (await store.GetMessagesAsync(null, "INBOX", "BJ")).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
