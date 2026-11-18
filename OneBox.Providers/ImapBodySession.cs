using System.Diagnostics;
using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace OneBox.Providers;

internal sealed class ImapBodySession
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly Timer _idleTimer;
    private ImapClient? _client;
    private IMailFolder? _folder;
    private long _lastUsed;

    public ImapBodySession()
    {
        _shutdownToken = _shutdown.Token;
        _idleTimer = new Timer(_ => CloseIfIdle());
    }

    public async Task<MimeMessage> FetchAsync(ImapMessageLocation location, Func<CancellationToken, Task<ImapClient>> connect,
        CancellationToken cancellationToken, Action<string>? onStage)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
        var token = operation.Token;
        onStage?.Invoke("imap_wait_for_body_session");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var reused = _client is { IsConnected: true, IsAuthenticated: true };
            if (reused)
            {
                try
                {
                    onStage?.Invoke("imap_reuse_authenticated_session");
                    return await FetchFromFolderAsync(location, token, onStage).ConfigureAwait(false);
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    error is IOException or ServiceNotConnectedException or ServiceNotAuthenticatedException or ImapProtocolException)
                {
                    CloseClient();
                    onStage?.Invoke("imap_reconnect");
                }
            }

            CloseClient();
            _client = await connect(token).ConfigureAwait(false);
            return await FetchFromFolderAsync(location, token, onStage).ConfigureAwait(false);
        }
        catch (MessageNotFoundException) { throw; }
        catch
        {
            CloseClient();
            throw;
        }
        finally
        {
            _lastUsed = Stopwatch.GetTimestamp();
            if (!_shutdownToken.IsCancellationRequested && _client is not null)
                _idleTimer.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    private async Task<MimeMessage> FetchFromFolderAsync(ImapMessageLocation location, CancellationToken cancellationToken,
        Action<string>? onStage)
    {
        var client = _client!;
        var folderName = location.FolderName ?? client.Inbox.FullName;
        if (_folder is not { IsOpen: true } folder || folder.FullName != folderName)
        {
            onStage?.Invoke("imap_open_folder");
            folder = location.FolderName is null ? client.Inbox :
                await client.GetFolderAsync(folderName, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            _folder = folder;
        }
        if (location.UidValidity is uint validity && folder.UidValidity != validity)
            throw new MessageNotFoundException("邮件所在文件夹已重建，请重新同步后打开邮件。");
        onStage?.Invoke("imap_download_and_parse_mime");
        return await folder.GetMessageAsync(location.Uid, cancellationToken).ConfigureAwait(false);
    }

    private void CloseIfIdle()
    {
        if (!_gate.Wait(0)) return;
        try
        {
            if (_client is null || _shutdownToken.IsCancellationRequested) return;
            var remaining = IdleTimeout - Stopwatch.GetElapsedTime(_lastUsed);
            if (remaining <= TimeSpan.Zero) CloseClient();
            else _idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _idleTimer.Dispose();
            CloseClient();
            _shutdown.Dispose();
        }
        finally { _gate.Release(); }
    }

    private void CloseClient()
    {
        _folder = null;
        _client?.Dispose();
        _client = null;
    }
}
