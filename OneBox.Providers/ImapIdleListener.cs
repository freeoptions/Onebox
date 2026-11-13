using System.Diagnostics;
using MailKit;
using MailKit.Net.Imap;
using OneBox.Core;
using IMailStore = OneBox.Core.IMailStore;

namespace OneBox.Providers;

public sealed record InboxListenerLog(string Text, string Kind, string Details, bool ShowInHistory = true);

public sealed class ImapIdleListener(Account account, ImapSmtpProvider provider, ICredentialVault vault, IMailStore store, bool junk)
{
    internal static (int Failures, TimeSpan Delay) NextRetry(int failures, TimeSpan connectedFor)
    {
        failures = connectedFor >= TimeSpan.FromMinutes(9) ? 1 : failures + 1;
        return (failures, failures <= 3 ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(5));
    }

    public async Task RunAsync(Action onChanged, Action<InboxListenerLog> report, CancellationToken ct)
    {
        var failures = 0;
        var label = junk ? "垃圾邮件" : "收件箱";
        while (!ct.IsCancellationRequested)
        {
            string? secret = null;
            var stage = "读取本机凭据";
            var session = Guid.NewGuid().ToString("N")[..8];
            var attemptClock = Stopwatch.StartNew();
            Stopwatch? connectedClock = null;
            string connectionInfo = "";
            void Log(string text, string kind, string details = "", bool showInHistory = true) => report(new InboxListenerLog(
                $"{account.Address} · {label}{text}", kind,
                $"会话：{session}\n账号：{account.Address}\n文件夹类型：{label}\n阶段：{stage}\n本次连接耗时：{attemptClock.Elapsed.TotalSeconds:F1}秒\nIDLE已确认连接时长：{connectedClock?.Elapsed.TotalSeconds ?? 0:F1}秒\n连续失败次数：{failures}\n{connectionInfo}\n{details}".Trim(),
                showInHistory));
            try
            {
                Log("监听开始连接", "状态", showInHistory: false);
                secret = vault.Read(account.Id);
                if (string.IsNullOrEmpty(secret)) throw new UnauthorizedAccessException("邮箱授权不可用");
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connecting.CancelAfter(TimeSpan.FromSeconds(30));
                var endpoint = ImapSmtpProvider.Servers(account.Address);
                connectionInfo = $"服务器：{endpoint.Imap}:{endpoint.ImapPort}";
                using var client = await provider.OpenImapAsync(account, secret, connecting.Token, value =>
                {
                    stage = value;
                    Log("监听连接阶段更新", "状态", showInHistory: false);
                }).ConfigureAwait(false);
                stage = "检查IDLE能力";
                connectionInfo += $"\nTLS：{client.SslProtocol}\n读取超时：{client.Timeout}毫秒\n服务器能力：{client.Capabilities}";
                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
                    throw new NotSupportedException("服务器未提供IDLE能力");
                stage = "查找监听文件夹";
                var folder = junk ? await ImapSmtpProvider.FindJunkFolderAsync(client, connecting.Token).ConfigureAwait(false) : client.Inbox;
                if (folder is null)
                {
                    Log("未发现可监听的文件夹", "状态");
                    return;
                }
                stage = "只读打开文件夹";
                await folder.OpenAsync(FolderAccess.ReadOnly, connecting.Token).ConfigureAwait(false);
                connectionInfo += $"\n实际文件夹：{folder.FullName}\nUIDVALIDITY：{folder.UidValidity}\nUIDNEXT：{folder.UidNext}";
                stage = "检查断线期间的邮件变化";
                var cursor = await store.GetCursorAsync(account.Id, junk ? "JUNK" : "INBOX", ct).ConfigureAwait(false);
                if (cursor is null || cursor.UidValidity != folder.UidValidity ||
                    folder.UidNext is UniqueId next && next.Id > (cursor.HighestUid ?? 0) + 1)
                    onChanged();
                Log("已打开文件夹，正在申请实时监听", "状态");
                while (!ct.IsCancellationRequested)
                {
                    stage = "申请IDLE";
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
                    try
                    {
                        var idleTask = client.IdleAsync(done.Token, ioCancellation.Token);
                        // IsIdle由MailKit在收到服务器的IDLE继续响应后设置，不能只根据登录成功推断。
                        while (!idleTask.IsCompleted && !client.IsIdle && !ioCancellation.IsCancellationRequested)
                            await Task.WhenAny(idleTask, Task.Delay(100)).ConfigureAwait(false);
                        if (client.IsIdle)
                        {
                            stage = "等待IDLE通知";
                            if (connectedClock is null)
                            {
                                connectedClock = Stopwatch.StartNew();
                                Log("实时监听已连接", "状态");
                            }
                            else Log("IDLE续接已确认", "状态", showInHistory: false);
                        }
                        await idleTask.ConfigureAwait(false);
                    }
                    finally { folder.CountChanged -= OnCountChanged; }
                    if (ct.IsCancellationRequested) break;
                    Log(changed ? "收到文件夹变化通知，触发同步" : "IDLE本轮结束，准备续接", "状态", showInHistory: false);
                    if (changed) onChanged();
                }
                Log("监听已主动停止", "状态", showInHistory: false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                Log("监听已主动停止", "状态", showInHistory: false);
                return;
            }
            catch (Exception error)
            {
                var failure = SyncFailure.Describe(error, account.Provider, stage, secret);
                if (error is NotSupportedException && stage == "检查IDLE能力")
                {
                    Log("不支持实时监听，仍可手动同步和5小时补查", "告警", failure.Details);
                    return;
                }
                if (failure.NeedsAuthorization)
                {
                    Log("实时监听需要更新授权", "告警", failure.Details);
                    return;
                }
                var retry = NextRetry(failures, connectedClock?.Elapsed ?? TimeSpan.Zero);
                failures = retry.Failures;
                var delay = retry.Delay;
                Log($"监听中断：{failure.Summary}，{(failures <= 3 ? $"5分钟后重试（{failures}/3）" : "本轮3次重试已耗尽，5小时后重连")}",
                    "告警", $"下次重连：{DateTimeOffset.Now.Add(delay):yyyy-MM-dd HH:mm:ss zzz}\n{failure.Details}");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                if (failures > 3) failures = 0;
            }
        }
    }
}
