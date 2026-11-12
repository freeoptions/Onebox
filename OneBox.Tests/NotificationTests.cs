using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;

namespace OneBox.Tests;

[TestClass]
public sealed class NotificationTests
{
    [TestMethod]
    public void MailListAndReaderDisplayTheSameLocalTime()
    {
        var receivedAt = new DateTimeOffset(2026, 11, 12, 5, 40, 0, TimeSpan.Zero);
        var message = new OneBox.Core.MailMessage(Guid.NewGuid(), "1", "INBOX", "主题", "", "", "",
            receivedAt, "", false, false, false);
        var row = new MessageRow(message, "邮箱");
        var model = new MainViewModel();
        model.ShowMessage(message);
        Assert.AreEqual(receivedAt.LocalDateTime, row.ReceivedAt);
        Assert.AreEqual(row.ReceivedAt.ToString("yyyy年M月d日 HH:mm"), model.SelectedDateText);
    }

    [TestMethod]
    public void LatestNoticeStaysVisibleAndHistoryRemainsWithinLimit()
    {
        var model = new MainViewModel();
        var before = DateTimeOffset.Now;
        model.Notify("附件检查中", "进度");
        model.Notify("图片已保存", "成功");
        Assert.AreEqual("图片已保存", model.NoticeText);
        Assert.AreEqual("成功", model.NoticeKind);
        var savedNotice = model.RecentNoticeList[0];
        Assert.IsTrue(savedNotice.RecordedAt >= before && savedNotice.RecordedAt <= DateTimeOffset.Now);
        Assert.AreEqual(savedNotice.TimeText, model.NoticeTimeText);
        Assert.AreEqual(savedNotice.RecordedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"), model.NoticeTimeText);
        Assert.AreEqual("图片已保存", model.NoticeText);
        for (var index = 0; index < 25; index++) model.Notify($"消息{index}", "告警");
        Assert.AreEqual(20, model.RecentNoticeList.Count);
        Assert.AreEqual("消息24", model.RecentNoticeList[0].Text);
        Assert.AreEqual("告警", model.RecentNoticeList[0].Kind);
        Assert.AreEqual("消息15", model.RecentNoticeList[9].Text);
        Assert.AreEqual("消息5", model.RecentNoticeList[19].Text);
        Assert.AreEqual("消息24", model.NoticeText);
        Assert.AreEqual(model.RecentNoticeList[0].TimeText, model.NoticeTimeText);
        Assert.AreEqual(savedNotice.TimeText, savedNotice.RecordedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
        model.Notify("下载失败", "失败");
        Assert.AreEqual("下载失败", model.NoticeText);
        Assert.AreEqual("失败", model.NoticeKind);
        Assert.AreEqual("下载失败", model.RecentNoticeList[0].Text);
        Assert.AreEqual("失败", model.RecentNoticeList[0].Kind);
        Assert.AreEqual(model.RecentNoticeList[0].TimeText, model.NoticeTimeText);
    }
}
