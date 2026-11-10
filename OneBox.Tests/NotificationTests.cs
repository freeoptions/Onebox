using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;

namespace OneBox.Tests;

[TestClass]
public sealed class NotificationTests
{
    [TestMethod]
    public void LatestNoticeAppearsImmediatelyAndHistoryRotatesWithinLimit()
    {
        var model = new MainViewModel();
        model.Notify("附件检查中", "进度");
        model.Notify("图片已保存", "成功");
        Assert.AreEqual("图片已保存", model.NoticeText);
        Assert.AreEqual("成功", model.NoticeKind);
        model.CycleNotice();
        Assert.AreEqual("图片已保存", model.NoticeText);
        for (var index = 0; index < 25; index++) model.Notify($"消息{index}", "告警");
        Assert.AreEqual(20, model.RecentNoticeList.Count);
        Assert.AreEqual(new NoticeEntry("消息24", "告警"), model.RecentNoticeList[0]);
        Assert.AreEqual(new NoticeEntry("消息15", "告警"), model.RecentNoticeList[9]);
        Assert.AreEqual(new NoticeEntry("消息5", "告警"), model.RecentNoticeList[19]);
        Assert.AreEqual("消息24", model.NoticeText);
        model.CycleNotice();
        Assert.AreEqual("消息5", model.NoticeText);
        model.Notify("下载失败", "失败");
        Assert.AreEqual("下载失败", model.NoticeText);
        Assert.AreEqual("失败", model.NoticeKind);
        Assert.AreEqual(new NoticeEntry("下载失败", "失败"), model.RecentNoticeList[0]);
    }
}
