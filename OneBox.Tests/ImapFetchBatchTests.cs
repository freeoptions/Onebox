using MailKit;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Providers;

namespace OneBox.Tests;

[TestClass]
public sealed class ImapFetchBatchTests
{
    [TestMethod]
    public void SparseDescendingUidsProduceShortAscendingCommandsWithoutLosingMessages()
    {
        var uidList = Enumerable.Range(0, 403).Select(index => new UniqueId(uint.MaxValue - 1 - (uint)(index * 3))).ToList();
        var batchList = ImapSmtpProvider.CreateFetchBatches(uidList).ToList();
        Assert.IsGreaterThan(1, batchList.Count);
        foreach (var batch in batchList)
        {
            Assert.IsLessThanOrEqualTo(50, batch.Count, "单次读取包含过多UID");
            Assert.IsLessThanOrEqualTo(550, UniqueIdSet.ToString(batch).Length, "UID命令参数过长");
            CollectionAssert.AreEqual(batch.OrderBy(uid => uid.Id).ToArray(), batch.ToArray(), "UID必须按正序发送");
        }
        CollectionAssert.AreEquivalent(uidList.ToArray(), batchList.SelectMany(batch => batch).ToArray(), "分批不能丢失或重复读取邮件");
    }

    [TestMethod]
    public void ConsecutiveUidsUseAscendingRangesAndDuplicateUidsAreFetchedOnce()
    {
        var uidList = Enumerable.Range(1, 120).Reverse().Select(index => new UniqueId((uint)index)).ToList();
        uidList.Add(new UniqueId(1));
        var batchList = ImapSmtpProvider.CreateFetchBatches(uidList).ToList();
        Assert.AreEqual(3, batchList.Count);
        Assert.AreEqual("1:50", UniqueIdSet.ToString(batchList[0]));
        Assert.AreEqual("51:100", UniqueIdSet.ToString(batchList[1]));
        Assert.AreEqual("101:120", UniqueIdSet.ToString(batchList[2]));
        Assert.AreEqual(120, batchList.Sum(batch => batch.Count));
    }

    [TestMethod]
    public void EmptySearchProducesNoFetchRequest()
    {
        Assert.AreEqual(0, ImapSmtpProvider.CreateFetchBatches(Array.Empty<UniqueId>()).Count());
    }
}
