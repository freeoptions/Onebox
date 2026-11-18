using System.Diagnostics;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using OneBox.Core;
using IMailStore = OneBox.Core.IMailStore;

namespace OneBox.Providers;

public sealed record InboxListenerLog(string Text, string Kind, string Details, bool ShowInHistory = true);

public sealed class ImapIdleListener(Account account, ImapSmtpProvider provider, ICredentialVault vault, IMailStore store, bool junk)
{
    internal static async Task<bool> HasNewMessagesAsync(IMailFolder folder, SyncCursor? cursor, CancellationToken ct)
    {
        if (cursor is null || cursor.UidValidity != folder.UidValidity) return true;
        var highestUid = cursor.HighestUid ?? 0;
        if (highestUid >= uint.MaxValue) return false;
        var range = new UniqueIdRange(new UniqueId((uint)Math.Max(1, highestUid + 1)), UniqueId.MaxValue);
        var uidList = await folder.SearchAsync(SearchQuery.Uids(range), ct).ConfigureAwait(false);
        // IMAP的n:*在n大于当前最大UID时也可能返回最后一封，必须再比较UID。
        return uidList.Any(uid => uid.Id > highestUid);
    }

    internal static async Task<bool> WaitForChangesAsync(ImapClient client, IMailFolder folder,
        Func<CancellationToken, Task<SyncCursor?>> readCursor, Action onIdleReady, CancellationToken ct,
        TimeSpan? checkInterval = null)
    {
        var interval = checkInterval ?? TimeSpan.FromSeconds(30);
        if (!client.Capabilities.HasFlag(ImapCapabilities.Idle))
        {
            onIdleReady();
            await Task.Delay(interval, ct).ConfigureAwait(false);
            using var pollTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pollTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.NoOpAsync(pollTimeout.Token).ConfigureAwait(false);
            return await HasNewMessagesAsync(folder, await readCursor(pollTimeout.Token).ConfigureAwait(false),
                pollTimeout.Token).ConfigureAwait(false);
        }
        using var done = new CancellationTokenSource(interval);
        using var ioCancellation = new CancellationTokenSource();
        ioCancellation.CancelAfter((checkInterval ?? TimeSpan.FromSeconds(30)) + TimeSpan.FromSeconds(15));
        var changed = false;
        void OnCountChanged(object? sender, EventArgs e) { changed = true; done.Cancel(); }
        folder.CountChanged += OnCountChanged;
        // TLS必须先退出IDLE，再取消会话读写，不能直接取消正在读取的SslStream。
        using var cancelIdle = ct.Register(() =>
        {
            done.Cancel();
            ioCancellation.CancelAfter(TimeSpan.FromSeconds(5));
        });
        try
        {
            var idleTask = client.IdleAsync(done.Token, ioCancellation.Token);
            while (!idleTask.IsCompleted && !client.IsIdle)
                await Task.WhenAny(idleTask, Task.Delay(50, ioCancellation.Token)).ConfigureAwait(false);
            if (client.IsIdle) onIdleReady();
            await idleTask.ConfigureAwait(false);
        }
        finally { folder.CountChanged -= OnCountChanged; }
        ct.ThrowIfCancellationRequested();
        if (changed) return true;

        // 连接成功不代表服务器一定推送EXISTS；在同一连接上主动查询新增UID兜底。
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.NoOpAsync(timeout.Token).ConfigureAwait(false);
        var cursor = await readCursor(timeout.Token).ConfigureAwait(false);
        return await HasNewMessagesAsync(folder, cursor, timeout.Token).ConfigureAwait(false);
    }

    internal static (int Failures, TimeSpan Delay) NextRetry(int failures, TimeSpan connectedFor)
    {
        failures = connectedFor >= TimeSpan.FromMinutes(9) ? 1 : failures + 1;
        return (failures, TimeSpan.FromSeconds(failures switch { 1 => 5, 2 => 15, 3 => 30, _ => 60 }));
    }

    internal static TimeSpan? QuickRetryDelay(Exception error, int quickFailures)
    {
        if (quickFailures >= 2) return null;
        var interrupted = false;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is MailKit.Security.AuthenticationException or UnauthorizedAccessException or
                System.Security.Authentication.AuthenticationException or
                MailKit.Security.SslHandshakeException { ServerCertificate: not null }) return null;
            if (current is IOException || SyncFailure.IsUnexpectedDisconnect(current) || current is SocketException
                { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.NetworkReset })
                interrupted = true;
        }
        return interrupted ? TimeSpan.FromSeconds(quickFailures == 0 ? 1 : 3) : null;
    }

    public async Task RunAsync(Action onChanged, Action onConnected, Action<InboxListenerLog> report, CancellationToken ct)
    {
        var failures = 0;
        var quickFailures = 0;
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
                $"会话：{session}\n账号：{account.Address}\n文件夹类型：{label}\n阶段：{stage}\n本次连接耗时：{attemptClock.Elapsed.TotalSeconds:F1}秒\n自动收信已确认连接时长：{connectedClock?.Elapsed.TotalSeconds ?? 0:F1}秒\n连续失败次数：{failures}\n{connectionInfo}\n{details}".Trim(),
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
                var supportsIdle = client.Capabilities.HasFlag(ImapCapabilities.Idle);
                // 无IDLE的网易账号复用已登录连接，每分钟检查一次，避免频繁登录。
                var checkInterval = TimeSpan.FromSeconds(account.Provider == ProviderKind.NetEase && !supportsIdle ? 60 : 30);
                var seconds = checkInterval.TotalSeconds;
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
                if (await HasNewMessagesAsync(folder, cursor, connecting.Token).ConfigureAwait(false))
                    onChanged();
                Log(supportsIdle ? "已打开文件夹，正在申请实时监听" : $"服务器不支持IDLE，改用{seconds:0}秒主动检查", "状态");
                var healthClock = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested)
                {
                    stage = supportsIdle ? $"实时监听及{seconds:0}秒新邮件补查" : $"{seconds:0}秒自动收信检查";
                    var changed = await WaitForChangesAsync(client, folder,
                        token => store.GetCursorAsync(account.Id, junk ? "JUNK" : "INBOX", token), () =>
                    {
                        if (connectedClock is null)
                        {
                            connectedClock = Stopwatch.StartNew();
                            Log(supportsIdle ? $"实时监听已连接（{seconds:0}秒主动补查兜底）" : $"自动收信已连接（每{seconds:0}秒检查）", "状态");
                            onConnected();
                        }
                    }, ct, checkInterval).ConfigureAwait(false);
                    if (changed)
                    {
                        Log("检测到文件夹变化或新增UID，触发自动同步", "状态", showInHistory: false);
                        onChanged();
                    }
                    else if (healthClock.Elapsed >= TimeSpan.FromMinutes(9))
                    {
                        Log(supportsIdle ? "实时监听及主动补查正常" : "自动收信检查正常", "状态", showInHistory: false);
                        healthClock.Restart();
                    }
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
                if (failure.NeedsAuthorization)
                {
                    Log("实时监听需要更新授权", "告警", failure.Details);
                    return;
                }
                var connectedFor = connectedClock?.Elapsed ?? TimeSpan.Zero;
                if (connectedFor >= TimeSpan.FromMinutes(9)) quickFailures = 0;
                var quickDelay = QuickRetryDelay(error, quickFailures);
                if (quickDelay is TimeSpan shortDelay)
                {
                    quickFailures++;
                    Log($"监听中断：{failure.Summary}，{shortDelay.TotalSeconds:0}秒后快速重连（{quickFailures}/2）",
                        "告警", $"下次重连：{DateTimeOffset.Now.Add(shortDelay):yyyy-MM-dd HH:mm:ss zzz}\n{failure.Details}");
                    try { await Task.Delay(shortDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    continue;
                }
                quickFailures = 0;
                var retry = NextRetry(failures, connectedFor);
                failures = retry.Failures;
                var delay = retry.Delay;
                Log($"监听中断：{failure.Summary}，{delay.TotalSeconds:0}秒后重连（连续{failures}次）",
                    "告警", $"下次重连：{DateTimeOffset.Now.Add(delay):yyyy-MM-dd HH:mm:ss zzz}\n{failure.Details}");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
    }
}
