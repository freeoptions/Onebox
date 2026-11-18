using System.Windows.Threading;
using OneBox.Core;
using OneBox.Providers;

namespace OneBox.App;

public partial class MainWindow
{
    private sealed class BodyRequest(Task<MailMessage> task, CancellationTokenSource cancellation, ImapBodyQueue.Request order)
    {
        public Task<MailMessage> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ImapBodyQueue.Request Order { get; } = order;
        public int Waiters { get; set; }
    }

    private readonly Dictionary<string, BodyRequest> _bodyRequestMap = [];
    private readonly ImapBodyQueue _imapBodyQueue = new();
    private readonly DispatcherTimer _bodyPrefetchTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private CancellationTokenSource? _bodyPrefetchCancellation;
    private int _bodyPrefetchRevision;

    private void ScheduleBodyPrefetch()
    {
        _bodyPrefetchRevision++;
        _bodyPrefetchTimer.Stop();
        if (!_exiting && IsVisible) _bodyPrefetchTimer.Start();
    }

    private async void OnBodyPrefetchTick(object? sender, EventArgs e)
    {
        _bodyPrefetchTimer.Stop();
        if (_exiting || !IsVisible || _bodyPrefetchCancellation is not null || _bodyLoadCancellation is not null) return;
        var revision = _bodyPrefetchRevision;
        var selected = MessageList.SelectedItem as MessageRow;
        var start = selected is null ? 0 : _viewModel.Messages.IndexOf(selected) + 1;
        var candidateList = _viewModel.Messages.Skip(start).Take(3).Where(row => !row.HasCachedBody && !row.HasAttachments).ToArray();
        using var cancellation = new CancellationTokenSource();
        _bodyPrefetchCancellation = cancellation;
        try
        {
            foreach (var row in candidateList)
            {
                if (_exiting || !IsVisible || revision != _bodyPrefetchRevision || _bodyLoadCancellation is not null) break;
                if (row.HasCachedBody) continue;
                var account = _viewModel.Accounts.FirstOrDefault(item => item.Id == row.Message.AccountId);
                if (account is null || account.Status != ConnectionStatus.Online || !_providerMap.TryGetValue(account.Provider, out var provider))
                    continue;
                var secret = _vault.Read(account.Id);
                if (secret is null) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await LoadMessageBodyAsync(account, provider, secret, row.Message, MailBodySource.Prefetch, timeout.Token);
            }
        }
        catch (Exception) { /* 预取失败时，打开邮件仍可正常重试。 */ }
        finally
        {
            _bodyPrefetchCancellation = null;
            if (revision != _bodyPrefetchRevision) ScheduleBodyPrefetch();
        }
    }

    private async Task<MailMessage> LoadMessageBodyAsync(Account account, IMailProvider provider, string secret,
        MailMessage message, MailBodySource source, CancellationToken cancellationToken, Action<string>? onStage = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bodyRequestMap.TryGetValue(message.Key, out var request))
        {
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            var order = new ImapBodyQueue.Request(source);
            request = new BodyRequest(FetchAndCacheMessageBodyAsync(account, provider, secret, message, token, order, onStage),
                cancellation, order);
            _bodyRequestMap.Add(message.Key, request);
        }
        else
        {
            request.Order.Promote(source);
            onStage?.Invoke($"wait_for_inflight_body_{request.Order.Source.ToString().ToLowerInvariant()}");
        }

        request.Waiters++;
        try { return await request.Task.WaitAsync(cancellationToken); }
        finally
        {
            if (--request.Waiters == 0)
            {
                _bodyRequestMap.Remove(message.Key);
                _ = ReleaseBodyRequestAsync(request);
            }
        }
    }

    private async Task<MailMessage> FetchAndCacheMessageBodyAsync(Account account, IMailProvider provider, string secret,
        MailMessage message, CancellationToken cancellationToken, ImapBodyQueue.Request order, Action<string>? onStage)
    {
        var backgroundTiming = onStage is null ? new MailBodyTiming() : null;
        Action<string> recordStage = onStage ?? backgroundTiming!.StartStage;
        Task<MailMessage> FetchAsync()
        {
            recordStage("prepare_provider_request");
            return Task.Run(() => provider.GetMessageAsync(account, secret, message.ServerId, cancellationToken, recordStage),
                cancellationToken);
        }
        var outcome = "cache_ready";
        MailMessage fullMessage;
        try
        {
            fullMessage = await (provider is ImapSmtpProvider
                ? _imapBodyQueue.RunAsync(account.Id, order, FetchAsync, cancellationToken, recordStage)
                : FetchAsync());
        }
        catch (Exception error)
        {
            outcome = cancellationToken.IsCancellationRequested ? "cancelled" : error.GetType().Name;
            throw;
        }
        finally
        {
            if (backgroundTiming is not null)
            {
                var details = backgroundTiming.Finish(account.Provider.ToString(), false, outcome,
                    order.Source.ToString().ToLowerInvariant());
                _ = Task.Run(() => _mailBodyLog.TryAppend("正文加载", "邮件正文阶段耗时", details));
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (fullMessage.TextBody is null) fullMessage = fullMessage with { TextBody = "" };
        if (!_exiting && _viewModel.Accounts.Any(item => item.Id == account.Id))
        {
            var row = _viewModel.Messages.FirstOrDefault(item => item.Message.Key == message.Key);
            row?.CacheBody(fullMessage);
            _ = SaveBodyCacheAsync(fullMessage);
        }
        return fullMessage;
    }

    private static async Task ReleaseBodyRequestAsync(BodyRequest request)
    {
        using var cancellation = request.Cancellation;
        if (!request.Task.IsCompleted) await cancellation.CancelAsync().ConfigureAwait(false);
        try { await request.Task.ConfigureAwait(false); }
        catch (Exception) { /* 等待方都已退出。 */ }
    }
}
