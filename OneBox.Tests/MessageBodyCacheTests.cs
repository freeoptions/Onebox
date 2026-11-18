using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class MessageBodyCacheTests
{
    [TestMethod]
    public async Task LoadedBodyIsReusableInCurrentListAndAfterDatabaseReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var account = new Account(Guid.NewGuid(), "example@163.com", "测试账号", ProviderKind.NetEase);
        var message = new MailMessage(account.Id, "1", "INBOX", "测试", "", "sender@example.com", "", DateTimeOffset.UtcNow,
            "", true, true, false);
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            await store.UpsertMessagesAsync([message]);
            var row = new MessageRow(message, account.Address);
            Assert.IsFalse(row.HasCachedBody);
            row.CacheBody(message with { TextBody = "完整正文", IsRead = false, IsStarred = false });
            Assert.IsTrue(row.HasCachedBody);
            Assert.AreEqual("完整正文", row.Message.TextBody);
            Assert.IsTrue(row.Message.IsRead);
            Assert.IsTrue(row.Message.IsStarred);
            await store.CacheMessageBodyAsync(row.Message);
            await store.UpsertMessagesAsync([message]);
            var reloadedRow = new MessageRow((await store.GetMessagesAsync(account.Id, "INBOX", null)).Single(), account.Address);
            Assert.IsTrue(reloadedRow.HasCachedBody);
            Assert.AreEqual("完整正文", reloadedRow.Message.TextBody);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task BackgroundBodyCachingPreservesFlagUpdatesAndSearchIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var account = new Account(Guid.NewGuid(), "example@163.com", "测试账号", ProviderKind.NetEase);
        var message = new MailMessage(account.Id, "1", "INBOX", "测试", "", "sender@example.com", "",
            DateTimeOffset.UtcNow, "", false, false, false);
        var fullMessage = message with { TextBody = "北京会议", HtmlBody = "<p>北京会议</p>", HasAttachments = true };
        var readAction = new PendingAction(Guid.NewGuid(), account.Id, message.ServerId, PendingActionKind.MarkRead, DateTimeOffset.UtcNow);
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            await store.UpsertMessagesAsync([message]);
            await store.SaveMessageFlagAsync(readAction);
            await store.CacheMessageBodyAsync(fullMessage);
            await store.UpsertMessagesAsync([message with { IsRead = true, HasAttachments = true }]);
            await store.SaveMessageFlagAsync(readAction with { Id = Guid.NewGuid(), Kind = PendingActionKind.Star });
            await Assert.ThrowsExactlyAsync<SqliteException>(
                () => store.SaveMessageFlagAsync(readAction with { Kind = PendingActionKind.MarkUnread }));

            var saved = (await store.GetMessagesAsync(account.Id, "INBOX", "beijing")).Single();
            Assert.IsTrue(saved.IsRead);
            Assert.IsTrue(saved.IsStarred);
            Assert.IsTrue(saved.HasAttachments);
            Assert.AreEqual(fullMessage.HtmlBody, saved.HtmlBody);
            Assert.AreEqual(2, (await store.GetPendingAsync(account.Id)).Count);

            await store.ResetFolderAsync(account.Id, "INBOX");
            await store.CacheMessageBodyAsync(fullMessage);
            Assert.AreEqual(0, (await store.GetMessagesAsync(account.Id, "INBOX", null)).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void HtmlOnlyAndEmptyFetchedBodiesAreRecognizedAsCached()
    {
        var message = new MailMessage(Guid.NewGuid(), "1", "INBOX", "测试", "", "", "", DateTimeOffset.UtcNow, "", false, false, false);
        var htmlRow = new MessageRow(message with { HtmlBody = "<p>HTML正文</p>" }, "测试账号");
        Assert.IsTrue(htmlRow.HasCachedBody);
        var emptyRow = new MessageRow(message, "测试账号");
        emptyRow.CacheBody(message);
        Assert.IsTrue(emptyRow.HasCachedBody);
        Assert.AreEqual("", emptyRow.Message.TextBody);
    }

    [TestMethod]
    public void SelectingUncachedMailImmediatelyReplacesPreviousReaderContent()
    {
        var first = new MailMessage(Guid.NewGuid(), "1", "INBOX", "邮件A", "", "", "", DateTimeOffset.UtcNow, "", false, false, false, "A的正文");
        var second = first with { ServerId = "2", Subject = "邮件B", TextBody = null, Preview = "B的摘要" };
        var viewModel = new MainViewModel();
        viewModel.ShowMessage(first);
        viewModel.ShowMessage(second);
        viewModel.IsBodyLoading = true;
        Assert.AreEqual("邮件B", viewModel.SelectedSubject);
        Assert.AreEqual("B的摘要", viewModel.SelectedBody);
        Assert.IsTrue(viewModel.IsBodyLoading);
    }
}
