using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;

namespace OneBox.Tests;

[TestClass]
public sealed class InboxNavigationTests
{
    [TestMethod]
    public void ReaderNavigationHasCorrectBoundariesAndReturningPreservesListAndSearch()
    {
        var accountId = Guid.NewGuid();
        var viewModel = new MainViewModel { SearchText = "测试" };
        viewModel.ShowMessages(Enumerable.Range(1, 3).Select(index => new MailMessage(accountId, index.ToString(), "INBOX",
            $"测试{index}", "发件人", "sender@example.com", "", DateTimeOffset.UtcNow, "摘要", false, false, false, "正文")));
        Assert.IsTrue(viewModel.IsListVisible);
        viewModel.ShowMessage(viewModel.Messages[0].Message);
        Assert.IsFalse(viewModel.IsListVisible);
        Assert.IsFalse(viewModel.CanGoPrevious);
        Assert.IsTrue(viewModel.CanGoNext);
        Assert.AreSame(viewModel.Messages[1], viewModel.AdjacentMessage(1));
        viewModel.ShowMessage(viewModel.Messages[2].Message);
        Assert.IsTrue(viewModel.CanGoPrevious);
        Assert.IsFalse(viewModel.CanGoNext);
        viewModel.ShowMessage(null);
        Assert.IsTrue(viewModel.IsListVisible);
        Assert.AreEqual("测试", viewModel.SearchText);
        Assert.AreEqual(3, viewModel.Messages.Count);
    }

    [TestMethod]
    public void ReadStateUpdatesSameRowAndUnreadCountWithoutOpeningReaderAgain()
    {
        var viewModel = new MainViewModel();
        viewModel.ShowMessages([new MailMessage(Guid.NewGuid(), "1", "INBOX", "测试", "", "", "", DateTimeOffset.UtcNow,
            "", false, false, false, "缓存正文")]);
        var row = viewModel.Messages[0];
        row.UpdateFlags(true, true);
        viewModel.RefreshMailCounts();
        Assert.IsFalse(row.IsUnread);
        Assert.IsTrue(row.IsStarred);
        Assert.IsTrue(row.HasCachedBody);
        StringAssert.Contains(viewModel.MailCountText, "0封未读");
        row.UpdateFlags(false, true);
        viewModel.RefreshMailCounts();
        Assert.IsTrue(row.IsUnread);
        StringAssert.Contains(viewModel.MailCountText, "1封未读");
        Assert.AreSame(row, viewModel.Messages[0]);
        Assert.IsTrue(viewModel.IsListVisible);
    }
}
