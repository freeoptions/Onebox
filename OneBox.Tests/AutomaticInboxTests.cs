using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class AutomaticInboxTests
{
    [TestMethod]
    [DataRow(false, "11", true, true, ProviderKind.Qq)]
    [DataRow(false, "10", false, true, ProviderKind.Qq)]
    [DataRow(true, "11", true, true, ProviderKind.Qq)]
    [DataRow(false, "11", true, false, ProviderKind.Qq)]
    [DataRow(false, "10", false, false, ProviderKind.Qq)]
    [DataRow(true, "11", true, true, ProviderKind.NetEase)]
    [DataRow(false, "11", true, false, ProviderKind.NetEase)]
    [DataRow(false, "10", false, false, ProviderKind.NetEase)]
    public async Task ImapDetectsMailEvenWhenServerDoesNotPushExists(bool pushExists, string searchResult, bool expected,
        bool supportsIdle, ProviderKind kind)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cancellation.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var commands = new List<string>();
        async Task ServeAsync()
        {
            using var socket = await listener.AcceptTcpClientAsync(ct);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            var capabilities = supportsIdle ? "IMAP4rev1 IDLE" : "IMAP4rev1";
            if (kind == ProviderKind.NetEase) capabilities += " ID";
            await writer.WriteLineAsync($"* OK [CAPABILITY {capabilities}] ready");
            string? idleTag = null;
            var identified = false;
            while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct) is { } line)
            {
                commands.Add(line);
                var tag = line.Split(' ')[0];
                if (line == "DONE")
                {
                    await writer.WriteLineAsync($"{idleTag} OK idle finished");
                }
                else if (line.Contains(" LOGIN ", StringComparison.Ordinal))
                    await writer.WriteLineAsync($"{tag} OK [CAPABILITY {capabilities}] logged in");
                else if (line.Contains(" ID ", StringComparison.Ordinal))
                {
                    identified = true;
                    await writer.WriteLineAsync("* ID (\"name\" \"Coremail\")");
                    await writer.WriteLineAsync($"{tag} OK ID completed");
                }
                else if (line.Contains(" EXAMINE ", StringComparison.Ordinal))
                {
                    if (kind == ProviderKind.NetEase && !identified)
                    {
                        await writer.WriteLineAsync($"{tag} NO EXAMINE Unsafe Login");
                        continue;
                    }
                    await writer.WriteLineAsync("* FLAGS (\\Seen)\r\n* 1 EXISTS\r\n* 0 RECENT\r\n* OK [UIDVALIDITY 1] valid\r\n* OK [UIDNEXT 11] next");
                    await writer.WriteLineAsync($"{tag} OK [READ-ONLY] opened");
                }
                else if (line.EndsWith(" IDLE", StringComparison.Ordinal))
                {
                    idleTag = tag;
                    await writer.WriteLineAsync("+ idling");
                    if (pushExists) await writer.WriteLineAsync("* 2 EXISTS");
                }
                else if (line.Contains("UID SEARCH", StringComparison.Ordinal))
                {
                    await writer.WriteLineAsync($"* SEARCH {searchResult}");
                    await writer.WriteLineAsync($"{tag} OK search complete");
                }
                else await writer.WriteLineAsync($"{tag} OK completed");
            }
        }
        var serverTask = ServeAsync();
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, SecureSocketOptions.None, ct);
            await client.AuthenticateAsync("test@example.com", "test-only-password", ct);
            await new ImapSmtpProvider(kind).IdentifyClientAsync(client, ct);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            var cursor = new SyncCursor(Guid.NewGuid(), "INBOX", null, 1, 10, DateTimeOffset.UtcNow);
            var changed = await ImapIdleListener.WaitForChangesAsync(client, client.Inbox,
                _ => Task.FromResult<SyncCursor?>(cursor), () => { }, ct, TimeSpan.FromMilliseconds(300));
            Assert.AreEqual(expected, changed);
            Assert.AreEqual(supportsIdle, commands.Contains("DONE"), "必须先退出IDLE才能补查");
            if (!pushExists)
            {
                var doneIndex = commands.IndexOf("DONE");
                Assert.IsTrue(commands.FindIndex(line => line.Contains(" NOOP", StringComparison.Ordinal)) > doneIndex);
                Assert.IsTrue(commands.FindIndex(line => line.Contains("UID SEARCH", StringComparison.Ordinal)) > doneIndex);
            }
            if (kind == ProviderKind.NetEase)
            {
                var idIndex = commands.FindIndex(line => line.Contains(" ID ", StringComparison.Ordinal));
                Assert.IsTrue(idIndex > commands.FindIndex(line => line.Contains(" LOGIN ", StringComparison.Ordinal)));
                Assert.IsTrue(idIndex < commands.FindIndex(line => line.Contains(" EXAMINE ", StringComparison.Ordinal)),
                    "网易的ID命令必须在认证后、打开文件夹前发送");
                if (!supportsIdle)
                {
                    cursor = cursor with { HighestUid = long.Parse(searchResult) };
                    Assert.IsFalse(await ImapIdleListener.WaitForChangesAsync(client, client.Inbox,
                        _ => Task.FromResult<SyncCursor?>(cursor), () => { }, ct, TimeSpan.FromMilliseconds(20)),
                        "已同步邮件不能反复触发通知");
                    Assert.AreEqual(1, commands.Count(line => line.Contains(" LOGIN ", StringComparison.Ordinal)),
                        "多次补查应复用同一连接，不能每次重新登录网易邮箱");
                    Assert.AreEqual(1, commands.Count(line => line.Contains(" EXAMINE ", StringComparison.Ordinal)));
                    Assert.IsFalse(commands.Any(line => line.EndsWith(" IDLE", StringComparison.Ordinal)),
                        "不支持IDLE时应直接主动补查");
                }
            }
            await client.DisconnectAsync(false, ct);
            await serverTask.WaitAsync(ct);
        }
        finally
        {
            await cancellation.CancelAsync();
            listener.Stop();
            try { await serverTask; }
            catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task GmailAutomaticLoopRecoversFromNetworkFailureAndSignalsNewMailWithoutManualSync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-auto-test-{Guid.NewGuid():N}");
        var store = new SqliteMailStore(Path.Combine(directory, "mail.db"));
        var account = new Account(Guid.NewGuid(), "test@gmail.com", "test", ProviderKind.Gmail);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task? loop = null;
        try
        {
            await store.InitializeAsync();
            await store.UpsertAccountAsync(account);
            await store.SaveCursorAsync(new SyncCursor(account.Id, "INBOX", "100", null, null, DateTimeOffset.UtcNow));
            var probes = 0;
            var handler = new GmailHandler(_ =>
            {
                if (Interlocked.Increment(ref probes) == 1) throw new HttpRequestException("test-only disconnect");
                return """{"historyId":"101"}""";
            });
            var vault = new TestVault();
            var provider = new GmailProvider("test-client", "test-secret", vault, httpHandler: handler);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = false;
            var reportedFailure = false;
            loop = new GmailInboxListener(account, provider, vault, store).RunAsync(
                () => changed.TrySetResult(), () => connected = true,
                entry => { if (entry.Kind == "告警") reportedFailure = true; }, cancellation.Token, TimeSpan.FromMilliseconds(20));
            await changed.Task.WaitAsync(cancellation.Token);
            Assert.IsTrue(connected);
            Assert.IsTrue(reportedFailure);
            Assert.IsTrue(probes >= 2, "网络失败后应自动重试");
            Assert.AreEqual("100", (await store.GetCursorAsync(account.Id, "INBOX"))!.GmailHistoryId);
            await cancellation.CancelAsync();
            await loop;
        }
        finally
        {
            await cancellation.CancelAsync();
            if (loop is not null) await loop;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task GmailProfileProbeDetectsChangesWithoutAdvancingSavedHistory()
    {
        var vault = new TestVault();
        var account = new Account(Guid.NewGuid(), "test@gmail.com", "test", ProviderKind.Gmail);
        var cursor = new SyncCursor(account.Id, "INBOX", "100", null, null, DateTimeOffset.UtcNow);
        var profileHistory = "100";
        var handler = new GmailHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
            ? $$"""{"historyId":"{{profileHistory}}"}""" : throw new InvalidOperationException(request.RequestUri.ToString()));
        var provider = new GmailProvider("test-client", "test-secret", vault, httpHandler: handler);
        Assert.IsFalse(await provider.HasInboxChangesAsync(account, "test-refresh", cursor, CancellationToken.None));
        profileHistory = "101";
        Assert.IsTrue(await provider.HasInboxChangesAsync(account, "test-refresh", cursor, CancellationToken.None));
        Assert.AreEqual("100", cursor.GmailHistoryId);
        Assert.IsNull(provider.LatestHistoryId, "探测不能提交尚未同步的游标");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GmailDoesNotSkipMailArrivingDuringSync(bool incremental)
    {
        var account = new Account(Guid.NewGuid(), "test@gmail.com", "test", ProviderKind.Gmail);
        var profileReads = 0;
        var handler = new GmailHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/profile", StringComparison.Ordinal))
                return ++profileReads == 1 ? """{"historyId":"100"}""" : """{"historyId":"102"}""";
            if (path.EndsWith("/history", StringComparison.Ordinal))
                return """{"historyId":"101","history":[]}""";
            if (path.EndsWith("/messages", StringComparison.Ordinal)) return "{}";
            throw new InvalidOperationException(path);
        });
        var provider = new GmailProvider("test-client", "test-secret", new TestVault(), httpHandler: handler);
        var cursor = incremental ? new SyncCursor(account.Id, "INBOX", "99", null, null, DateTimeOffset.UtcNow) : null;
        await provider.GetRecentInboxAsync(account, "test-refresh", cursor, DateTimeOffset.UtcNow.AddDays(-90), CancellationToken.None);
        Assert.AreEqual(incremental ? "101" : "100", provider.LatestHistoryId,
            "游标只能覆盖本次实际读取的历史，不能记录随后到达的新邮件");
        var saved = new SyncCursor(account.Id, "INBOX", provider.LatestHistoryId, null, null, DateTimeOffset.UtcNow);
        Assert.IsTrue(await provider.HasInboxChangesAsync(account, "test-refresh", saved, CancellationToken.None));
    }

    private sealed class TestVault : ICredentialVault
    {
        public void Save(Guid accountId, string secret) { }
        public string? Read(Guid accountId) => "test-refresh";
        public void Delete(Guid accountId) { }
    }

    private sealed class GmailHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.Host == "oauth2.googleapis.com"
                ? """{"access_token":"test-access","expires_in":3600}""" : respond(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
