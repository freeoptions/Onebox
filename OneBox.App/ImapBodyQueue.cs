using OneBox.Core;

namespace OneBox.App;

internal enum MailBodySource { Reader, Notification, Prefetch }

internal sealed class ImapBodyQueue
{
    internal sealed class Request(MailBodySource source)
    {
        private volatile MailBodySource _priority = source;

        public MailBodySource Source { get; } = source;
        public MailBodySource Priority => _priority;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Promote(MailBodySource source)
        {
            if (source < _priority) _priority = source;
        }
    }

    private sealed class AccountQueue
    {
        public Request? Active { get; set; }
        public List<(Request Request, Action<string> OnStage)> WaitingList { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, AccountQueue> _accountQueueMap = [];

    public async Task<MailMessage> RunAsync(Guid accountId, Request request, Func<Task<MailMessage>> fetch,
        CancellationToken cancellationToken, Action<string> onStage)
    {
        AccountQueue queue;
        lock (_gate)
        {
            if (!_accountQueueMap.TryGetValue(accountId, out queue!))
                _accountQueueMap.Add(accountId, queue = new AccountQueue());
            if (queue.Active is null)
            {
                queue.Active = request;
                request.Ready.SetResult();
            }
            else
            {
                onStage(WaitStage(queue.Active));
                queue.WaitingList.Add((request, onStage));
            }
        }

        try
        {
            await request.Ready.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return await fetch();
        }
        finally
        {
            lock (_gate)
            {
                if (queue.Active == request)
                {
                    var next = queue.WaitingList.OrderBy(item => item.Request.Priority).FirstOrDefault();
                    queue.Active = next.Request;
                    if (queue.Active is null) _accountQueueMap.Remove(accountId);
                    else
                    {
                        queue.WaitingList.Remove(next);
                        var stage = WaitStage(queue.Active);
                        foreach (var waiting in queue.WaitingList) waiting.OnStage(stage);
                        queue.Active.Ready.SetResult();
                    }
                }
                else queue.WaitingList.RemoveAll(item => item.Request == request);
            }
        }
    }

    private static string WaitStage(Request active) => $"imap_queue_behind_{active.Source.ToString().ToLowerInvariant()}";
}
