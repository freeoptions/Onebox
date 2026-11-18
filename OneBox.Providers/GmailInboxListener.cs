using OneBox.Core;

namespace OneBox.Providers;

// 旧OAuth账号沿用现有授权，通过轻量historyId检查自动收信，无需另配云端Pub/Sub。
public sealed class GmailInboxListener(Account account, GmailProvider provider, ICredentialVault vault, IMailStore store)
{
    public Task RunAsync(Action onChanged, Action onConnected, Action<InboxListenerLog> report, CancellationToken ct) =>
        RunAsync(onChanged, onConnected, report, ct, TimeSpan.FromSeconds(15));

    internal async Task RunAsync(Action onChanged, Action onConnected, Action<InboxListenerLog> report, CancellationToken ct,
        TimeSpan checkInterval)
    {
        var connected = false;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            string? secret = null;
            var delay = checkInterval;
            try
            {
                secret = vault.Read(account.Id);
                if (string.IsNullOrEmpty(secret)) throw new UnauthorizedAccessException("邮箱授权不可用");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var cursor = await store.GetCursorAsync(account.Id, "INBOX", timeout.Token).ConfigureAwait(false);
                var changed = await provider.HasInboxChangesAsync(account, secret, cursor, timeout.Token).ConfigureAwait(false);
                if (!connected)
                {
                    report(new InboxListenerLog($"{account.Address} · Gmail自动收信已启用（每15秒检查）", "状态", "方式：Gmail API增量检查"));
                    connected = true;
                    onConnected();
                }
                failures = 0;
                if (changed) onChanged();
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                connected = false;
                var failure = SyncFailure.Describe(error, account.Provider, "检查Gmail新邮件", secret);
                if (failure.NeedsAuthorization)
                {
                    report(new InboxListenerLog($"{account.Address} · Gmail自动收信需要更新授权", "告警", failure.Details));
                    return;
                }
                failures = Math.Min(failures + 1, 3);
                delay = TimeSpan.FromTicks(checkInterval.Ticks * (1 << (failures - 1)));
                report(new InboxListenerLog($"{account.Address} · Gmail自动收信检查失败，{delay.TotalSeconds:0}秒后重试", "告警", failure.Details));
            }
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
