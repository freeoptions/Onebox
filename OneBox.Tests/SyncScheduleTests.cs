using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class SyncScheduleTests
{
    [TestMethod]
    public void SuccessfulSyncBecomesDueAtFiveHours()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new SyncSchedule(now);
        Assert.IsFalse(schedule.IsDue(now.AddHours(5).AddTicks(-1)));
        Assert.IsTrue(schedule.IsDue(now.AddHours(5)));
    }

    [TestMethod]
    public void FailureAllowsThreeFiveMinuteRetriesThenWaitsFiveHours()
    {
        var now = DateTimeOffset.UtcNow;
        var schedule = new SyncSchedule(now);
        for (var retry = 1; retry <= 3; retry++)
        {
            schedule = schedule.Failed(now, needsAuthorization: false);
            Assert.AreEqual(retry, schedule.Failures);
            Assert.AreEqual(now.AddMinutes(5), schedule.RetryAt);
            Assert.IsFalse(schedule.IsDue(now.AddMinutes(5).AddTicks(-1)));
            now = now.AddMinutes(5);
            Assert.IsTrue(schedule.IsDue(now));
            schedule = schedule.Start(now);
        }
        schedule = schedule.Failed(now, needsAuthorization: false);
        Assert.AreEqual(4, schedule.Failures);
        Assert.IsNull(schedule.RetryAt);
        Assert.IsFalse(schedule.IsDue(now.AddMinutes(5)));
        Assert.IsTrue(schedule.IsDue(now.AddHours(5)));
        Assert.AreEqual(0, schedule.Start(now.AddHours(5)).Failures);
    }

    [TestMethod]
    public void AuthorizationFailureDoesNotScheduleRetriesAndSuccessResetsFailures()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.IsNull(new SyncSchedule(now).Failed(now, needsAuthorization: true).RetryAt);
        var failedSchedule = new SyncSchedule(now).Failed(now, needsAuthorization: false);
        var successfulSchedule = new SyncSchedule(now.AddMinutes(5));
        Assert.IsNotNull(failedSchedule.RetryAt);
        Assert.AreEqual(0, successfulSchedule.Failures);
        Assert.IsNull(successfulSchedule.RetryAt);
        Assert.IsFalse(successfulSchedule.IsDue(now.AddHours(5)));
    }

    [TestMethod]
    public void RestartPreservesAccountTimesAndRetryLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sync-schedule.json");
        try
        {
            var settings = SyncScheduleSettings.Load(path);
            var successfulId = Guid.NewGuid();
            var failedId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            settings.AccountMap[successfulId] = new SyncSchedule(now);
            settings.AccountMap[failedId] = new SyncSchedule(now, 3, now.AddMinutes(5));
            settings.Save(path);
            settings = SyncScheduleSettings.Load(path);
            Assert.IsFalse(settings.AccountMap[successfulId].IsDue(now.AddHours(1)));
            var retry = settings.AccountMap[failedId].Start(now.AddMinutes(5)).Failed(now.AddMinutes(5), false);
            Assert.AreEqual(4, retry.Failures);
            Assert.IsNull(retry.RetryAt);
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
