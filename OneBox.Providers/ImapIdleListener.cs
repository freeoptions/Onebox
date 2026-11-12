using MailKit;
using MailKit.Net.Imap;
using OneBox.Core;
using IMailStore = OneBox.Core.IMailStore;

namespace OneBox.Providers;

public sealed class ImapIdleListener(Account account, ImapSmtpProvider provider, ICredentialVault vault, IMailStore store, bool junk)
{
    public async Task RunAsync(Action onChanged, Action<string, string> notify, CancellationToken ct)
    {
        var failures = 0;
        var label = junk ? "垃圾邮件" : "收件箱";
        while (!ct.IsCancellationRequested)
        {
            string? secret = null;
            try
            {
                secret = vault.Read(account.Id);
                if (string.IsNullOrEmpty(secret)) throw new UnauthorizedAccessException("邮箱授权不可用");
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connecting.CancelAfter(TimeSpan.FromSeconds(30));
                using var client = await provider.OpenImapAsync(account, secret, connecting.Token).ConfigureAwait(false);
                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                    throw new NotSupportedException("服务器未提供IDLE能力");
                var folder = junk ? await ImapSmtpProvider.FindJunkFolderAsync(client, connecting.Token).ConfigureAwait(false) : client.Inbox;
                if (folder is null) return;
                await folder.OpenAsync(FolderAccess.ReadOnly, connecting.Token).ConfigureAwait(false);
                var cursor = await store.GetCursorAsync(account.Id, junk ? "JUNK" : "INBOX", ct).ConfigureAwait(false);
                if (cursor is null || cursor.UidValidity != folder.UidValidity ||
                    folder.UidNext is UniqueId next && next.Id > (cursor.HighestUid ?? 0) + 1)
                    onChanged();
                failures = 0;
                notify($"{account.Address} · {label}实时监听已连接", "状态");
                while (!ct.IsCancellationRequested)
                {
                    using var done = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    using var ioCancellation = new CancellationTokenSource();
                    var changed = false;
                    void OnCountChanged(object? sender, EventArgs e) { changed = true; done.Cancel(); }
                    folder.CountChanged += OnCountChanged;
                    // TLS连接必须先结束IDLE，再取消会话读写。
                    using var cancelIdle = ct.Register(() =>
                    {
                        done.Cancel();
                        ioCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    });
                    try { await client.IdleAsync(done.Token, ioCancellation.Token).ConfigureAwait(false); }
                    finally { folder.CountChanged -= OnCountChanged; }
                    if (changed) onChanged();
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (NotSupportedException)
            {
                notify($"{account.Address} · {label}不支持实时监听，仍可手动同步和5小时补查", "告警");
                return;
            }
            catch (Exception error)
            {
                var failure = SyncFailure.Describe(error, account.Provider, $"监听{label}", secret);
                if (failure.NeedsAuthorization)
                {
                    notify($"{account.Address} · 实时监听需要更新授权", "告警");
                    return;
                }
                var delay = ++failures <= 3 ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(5);
                if (failures > 3) failures = 0;
                notify($"{account.Address} · {label}监听中断：{failure.Summary}，{delay.TotalMinutes:0}分钟后重连", "告警");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
    }
}
