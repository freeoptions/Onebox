using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class FavoritesTests
{
    [TestMethod]
    public async Task FavoritesFilterBeforeLimitAndSupportSearchAcrossAccountsAndUnstar()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var first = new Account(Guid.NewGuid(), "first@qq.com", "甲", ProviderKind.Qq);
        var second = new Account(Guid.NewGuid(), "second@163.com", "乙", ProviderKind.NetEase);
        var now = DateTimeOffset.UtcNow;
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(first);
            await store.UpsertAccountAsync(second);
            var recentMessageList = Enumerable.Range(0, 210).Select(index =>
                new MailMessage(first.Id, $"recent-{index}", "INBOX", "北京未收藏", "发件人", "sender@example.com", "",
                    now.AddMinutes(-index), "", false, false, false)).ToList();
            var olderFavorite = new MailMessage(first.Id, "starred", "INBOX", "北京会议", "甲", "first@example.com", "",
                now.AddDays(-2), "", false, true, false, "收藏正文");
            var newerFavorite = new MailMessage(second.Id, "starred", "INBOX", "上海合同", "乙", "second@example.com", "",
                now.AddDays(-1), "", true, true, false, "合同正文");
            await store.UpsertMessagesAsync(recentMessageList.Concat([olderFavorite, newerFavorite]));
            Assert.IsTrue((await store.GetMessagesAsync(null, "INBOX", null)).All(message => !message.IsStarred));
            var favoriteMessageList = await store.GetMessagesAsync(null, "INBOX", null, starredOnly: true);
            Assert.AreEqual(2, favoriteMessageList.Count);
            Assert.AreEqual(second.Id, favoriteMessageList[0].AccountId);
            Assert.AreEqual(first.Id, favoriteMessageList[1].AccountId);
            Assert.AreEqual("收藏正文", favoriteMessageList[1].TextBody);
            Assert.AreEqual(second.Id, (await store.GetMessagesAsync(null, "INBOX", null, limit: 1, starredOnly: true)).Single().AccountId);
            Assert.AreEqual(first.Id, (await store.GetMessagesAsync(null, "INBOX", "BJ", starredOnly: true)).Single().AccountId);
            Assert.AreEqual(second.Id, (await store.GetMessagesAsync(null, "INBOX", "second@example.com", starredOnly: true)).Single().AccountId);
            Assert.AreEqual(1, (await store.GetMessagesAsync(first.Id, "INBOX", null, starredOnly: true)).Count);
            Assert.AreEqual(0, (await store.GetMessagesAsync(null, "INBOX", "不存在", starredOnly: true)).Count);
            await store.UpsertMessagesAsync([olderFavorite with { IsStarred = false }]);
            Assert.AreEqual(second.Id, (await store.GetMessagesAsync(null, "INBOX", null, starredOnly: true)).Single().AccountId);
            Assert.IsFalse((await store.GetMessagesAsync(first.Id, "INBOX", "北京会议")).Single().IsStarred);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RemovingLastFavoriteRefreshesEmptyStateCountsAndReaderNavigation()
    {
        var viewModel = new MainViewModel();
        viewModel.ShowMessages([new MailMessage(Guid.NewGuid(), "1", "INBOX", "收藏", "", "", "", DateTimeOffset.UtcNow,
            "", false, true, false, "正文")]);
        viewModel.ShowMessage(viewModel.Messages[0].Message);
        var emptyStateChanged = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.HasMessages)) emptyStateChanged = true;
        };
        viewModel.ShowMessage(null);
        viewModel.Messages.RemoveAt(0);
        viewModel.RefreshMailCounts();
        Assert.IsTrue(emptyStateChanged);
        Assert.IsFalse(viewModel.HasMessages);
        Assert.IsTrue(viewModel.IsListVisible);
        Assert.IsFalse(viewModel.CanGoPrevious);
        Assert.IsFalse(viewModel.CanGoNext);
        StringAssert.Contains(viewModel.MailCountText, "共0封");
    }
}
