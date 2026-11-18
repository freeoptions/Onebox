using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.App;
using OneBox.Core;

namespace OneBox.Tests;

[TestClass]
public sealed class ImapBodyQueueTests
{
    [TestMethod]
    public async Task ReaderRunsBeforeQueuedNotificationAndPrefetch()
    {
        var queue = new ImapBodyQueue();
        var accountId = Guid.NewGuid();
        var message = CreateMessage(accountId);
        var activeCompletion = new TaskCompletionSource<MailMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionQueue = new ConcurrentQueue<MailBodySource>();
        var stageQueue = new ConcurrentQueue<string>();
        var active = queue.RunAsync(accountId, new(MailBodySource.Prefetch), () => activeCompletion.Task,
            CancellationToken.None, _ => { });
        Task<MailMessage> Enqueue(MailBodySource source) =>
            queue.RunAsync(accountId, new(source), () =>
            {
                executionQueue.Enqueue(source);
                return Task.FromResult(message);
            }, CancellationToken.None, stageQueue.Enqueue);

        var prefetch = Enqueue(MailBodySource.Prefetch);
        var notification = Enqueue(MailBodySource.Notification);
        var reader = Enqueue(MailBodySource.Reader);
        Assert.IsTrue(executionQueue.IsEmpty, "运行中的IMAP请求不能被抢占");
        activeCompletion.SetResult(message);
        await Task.WhenAll(active, prefetch, notification, reader).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { MailBodySource.Reader, MailBodySource.Notification, MailBodySource.Prefetch },
            executionQueue.ToArray());
        CollectionAssert.Contains(stageQueue.ToArray(), "imap_queue_behind_prefetch");
        CollectionAssert.Contains(stageQueue.ToArray(), "imap_queue_behind_reader");
    }

    [TestMethod]
    public async Task OpeningQueuedPrefetchPromotesItAndCancelledRequestDoesNotRun()
    {
        var queue = new ImapBodyQueue();
        var accountId = Guid.NewGuid();
        var message = CreateMessage(accountId);
        var activeCompletion = new TaskCompletionSource<MailMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionQueue = new ConcurrentQueue<string>();
        using var cancellation = new CancellationTokenSource();
        var active = queue.RunAsync(accountId, new(MailBodySource.Reader), () => activeCompletion.Task,
            CancellationToken.None, _ => { });
        Task<MailMessage> Enqueue(string name, ImapBodyQueue.Request request, CancellationToken token = default) =>
            queue.RunAsync(accountId, request, () =>
            {
                executionQueue.Enqueue(name);
                return Task.FromResult(message);
            }, token, _ => { });

        var notification = Enqueue("notification", new(MailBodySource.Notification));
        var prefetchOrder = new ImapBodyQueue.Request(MailBodySource.Prefetch);
        var prefetch = Enqueue("promoted", prefetchOrder);
        prefetchOrder.Promote(MailBodySource.Reader);
        var cancelled = Enqueue("cancelled", new(MailBodySource.Reader), cancellation.Token);
        cancellation.Cancel();
        try { await cancelled.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Assert.IsTrue(cancelled.IsCanceled);
        activeCompletion.SetResult(message);
        await Task.WhenAll(active, notification, prefetch).WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "promoted", "notification" }, executionQueue.ToArray());
    }

    private static MailMessage CreateMessage(Guid accountId) =>
        new(accountId, "1", "INBOX", "", "", "", "", DateTimeOffset.UnixEpoch, "", false, false, false);
}
