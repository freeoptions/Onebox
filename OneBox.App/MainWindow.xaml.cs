using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using Microsoft.Web.WebView2.Core;

namespace OneBox.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly IMailStore _store;
    private readonly string _dataDirectory;
    private readonly string _readerSettingsPath;
    private ReaderSettings _readerSettings = new();
    private readonly WindowsCredentialVault _vault = new();
    private readonly Dictionary<ProviderKind, IMailProvider> _providerMap = new()
    {
        [ProviderKind.Qq] = new ImapSmtpProvider(ProviderKind.Qq),
        [ProviderKind.NetEase] = new ImapSmtpProvider(ProviderKind.NetEase)
    };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromHours(5) };
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _noticePopupCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool _savingImage;
    private readonly Forms.NotifyIcon _tray;
    private CoreWebView2Environment? _webEnvironment;
    private string? _googleClientId;
    private string? _googleClientSecret;
    private bool _exiting;
    private bool _syncing;
    private bool _loadingAccounts;
    private bool _allowRemoteImages;
    private CancellationTokenSource? _bodyLoadCancellation;
    private Guid? _selectedAccount;
    private bool _starredOnly;
    private double _listScrollOffset;
    private readonly SemaphoreSlim _flagWriteLock = new(1, 1);
    private readonly SemaphoreSlim _pendingWriteLock = new(1, 1);
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset RetryAt)> _retryMap = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _dataDirectory = AppContext.BaseDirectory;
        _readerSettingsPath = System.IO.Path.Combine(_dataDirectory, "reader-settings.json");
        _store = new SqliteMailStore(System.IO.Path.Combine(_dataDirectory, "mail.db"));
        var clientIdFile = System.IO.Path.Combine(_dataDirectory, "google-client-id.txt");
        if (System.IO.File.Exists(clientIdFile))
        {
            var clientId = System.IO.File.ReadAllText(clientIdFile).Trim();
            if (clientId.Length > 0)
            {
                _googleClientId = clientId;
                _googleClientSecret = _vault.ReadGoogleClientSecret(clientId);
                if (!string.IsNullOrWhiteSpace(_googleClientSecret))
                    _providerMap[ProviderKind.Gmail] = new GmailProvider(clientId, _googleClientSecret, _vault);
            }
        }
        _tray = new Forms.NotifyIcon
        {
            Text = "OneBox - 多邮箱收发",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button != Forms.MouseButtons.Left) return;
            Dispatcher.Invoke(() => { if (IsVisible) Hide(); else ShowFromTray(); });
        };
        _pollTimer.Tick += async (_, _) => await SyncAllAsync();
        _retryTimer.Tick += async (_, _) =>
        {
            UpdateSyncProblem();
            if (!_syncing && _retryMap.Values.Any(retry => retry.RetryAt <= DateTimeOffset.UtcNow))
                await SyncAllAsync(retryOnly: true);
        };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await LoadMessagesAsync(); };
        _viewModel.SearchChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _noticeTimer.Tick += (_, _) => { if (!NoticePanel.IsMouseOver && !NoticeHistoryPopup.IsOpen) _viewModel.CycleNotice(); };
        _noticePopupCloseTimer.Tick += (_, _) =>
        {
            _noticePopupCloseTimer.Stop();
            if (!NoticeHistoryButton.IsMouseOver && !NoticeHistoryContent.IsMouseOver) NoticeHistoryPopup.IsOpen = false;
        };
        Deactivated += (_, _) => { NoticeHistoryPopup.IsOpen = false; _noticePopupCloseTimer.Stop(); };
        _noticeTimer.Start();
    }

    private void OnNextNotice(object sender, RoutedEventArgs e) => _viewModel.CycleNotice();

    private void OnNoticeHistoryMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _noticePopupCloseTimer.Stop();
        NoticeHistoryPopup.IsOpen = true;
    }

    private void OnNoticeHistoryMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _noticePopupCloseTimer.Start();

    private void Notify(string text, string kind)
    {
        _viewModel.Notify(text, kind);
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示软件", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(() => { _exiting = true; Close(); }));
        return menu;
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyReaderSettings(ReaderSettings.Load(_readerSettingsPath));
            await _store.InitializeAsync();
            await LoadAccountsAsync();
            await LoadMessagesAsync();
            _pollTimer.Start();
            _retryTimer.Start();
            await SyncAllAsync(force: true);
        }
        catch (Exception error)
        {
            _viewModel.StatusText = "本地数据不可用";
            MessageBox.Show(this, $"无法打开本地邮箱数据：{error.Message}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadAccountsAsync()
    {
        var accounts = await _store.GetAccountsAsync();
        _loadingAccounts = true;
        try
        {
            _viewModel.Accounts.Clear();
            foreach (var account in accounts) _viewModel.Accounts.Add(account);
            AccountsList.SelectedItem = accounts.FirstOrDefault(account => account.Id == _selectedAccount);
        }
        finally { _loadingAccounts = false; }
        UpdateNavigationSelection();
        UpdateSyncProblem();
    }

    private async Task LoadMessagesAsync()
    {
        var selectedKey = (MessageList.SelectedItem as MessageRow)?.Message.Key;
        var messages = await _store.GetMessagesAsync(_selectedAccount, "INBOX", _viewModel.SearchText, starredOnly: _starredOnly);
        _viewModel.ShowMessages(messages);
        if (selectedKey is not null)
            MessageList.SelectedItem = _viewModel.Messages.FirstOrDefault(row => row.Message.Key == selectedKey);
        _viewModel.CurrentFolderTitle = _starredOnly ? "收藏夹" : _selectedAccount is Guid id
            ? _viewModel.Accounts.FirstOrDefault(account => account.Id == id)?.DisplayName ?? "收件箱"
            : "所有收件箱";
        UpdateSyncProblem();
    }

    private async Task SyncAllAsync(bool force = false, Guid? accountId = null, bool retryOnly = false)
    {
        if (_syncing) return;
        _syncing = true;
        _viewModel.IsSyncing = true;
        _viewModel.StatusText = "正在同步邮件";
        UpdateSyncProblem();
        try
        {
            foreach (var account in _viewModel.Accounts.ToArray())
            {
                if (accountId is not null && account.Id != accountId) continue;
                if (retryOnly && !_retryMap.ContainsKey(account.Id)) continue;
                if (!force && _retryMap.TryGetValue(account.Id, out var retry) && retry.RetryAt > DateTimeOffset.UtcNow)
                    continue;
                if (!_providerMap.TryGetValue(account.Provider, out var provider))
                {
                    _retryMap.Remove(account.Id);
                    await _store.UpsertAccountAsync(account with
                    {
                        Status = ConnectionStatus.NeedsAuthorization,
                        LastError = "邮箱授权配置不完整。请点击“更新授权”，补充配置后重新同步。"
                    });
                    continue;
                }
                string? secret = null;
                var stage = "读取本机凭据";
                try
                {
                    secret = _vault.Read(account.Id);
                    if (string.IsNullOrEmpty(secret))
                        throw new UnauthorizedAccessException("Windows凭据管理器中没有此账号的有效授权，请更新授权。");
                    stage = "回写已读和星标状态";
                    await ProcessPendingAsync(account, provider, secret);
                    stage = "读取收件箱";
                    var cursor = await _store.GetCursorAsync(account.Id, "INBOX");
                    var messageList = await provider.GetRecentInboxAsync(account, secret, cursor, DateTimeOffset.UtcNow.AddDays(-90), CancellationToken.None);
                    stage = "保存本地邮件";
                    var validity = (provider as ImapSmtpProvider)?.CurrentUidValidity;
                    if (validity is not null && cursor?.UidValidity is not null && validity != cursor.UidValidity)
                        await _store.ResetFolderAsync(account.Id, "INBOX");
                    await _store.UpsertMessagesAsync(messageList);
                    await _store.SaveCursorAsync(new SyncCursor(account.Id, "INBOX",
                        (provider as GmailProvider)?.LatestHistoryId, validity,
                        messageList.Select(message => long.TryParse(message.ServerId, out var uid) ? uid : 0).DefaultIfEmpty(0).Max(), DateTimeOffset.UtcNow));
                    await _store.UpsertAccountAsync(account with { Status = ConnectionStatus.Online, LastError = null });
                    _retryMap.Remove(account.Id);
                }
                catch (Exception error)
                {
                    var failure = SyncFailure.Describe(error, account.Provider, stage, secret, _googleClientSecret);
                    if (failure.NeedsAuthorization) _retryMap.Remove(account.Id);
                    else
                    {
                        var failureCount = _retryMap.TryGetValue(account.Id, out var previous) ? previous.Failures + 1 : 1;
                        var delay = TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Min(failureCount - 1, 4))));
                        _retryMap[account.Id] = (failureCount, DateTimeOffset.UtcNow + delay);
                    }
                    await _store.UpsertAccountAsync(account with
                    {
                        Status = failure.NeedsAuthorization ? ConnectionStatus.NeedsAuthorization : ConnectionStatus.Failed,
                        LastError = failure.DisplayText
                    });
                }
            }
            await LoadAccountsAsync();
            await LoadMessagesAsync();
            var failures = _viewModel.Accounts.Count(account => account.Status is ConnectionStatus.Failed or ConnectionStatus.NeedsAuthorization);
            _viewModel.StatusText = _viewModel.Accounts.Count == 0 ? "尚未添加账号" :
                failures == 0 ? $"已同步 · {DateTime.Now:HH:mm}" : $"{failures}个账号需要处理";
            Notify(_viewModel.StatusText, failures == 0 ? "成功" : "告警");
        }
        catch (Exception error)
        {
            _viewModel.StatusText = "同步未完成 · 本地数据异常";
            var failure = SyncFailure.Describe(error, ProviderKind.Gmail, "读取或保存本地邮箱数据", _googleClientSecret);
            MessageBox.Show(this, failure.DisplayText, "同步未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _syncing = false;
            _viewModel.IsSyncing = false;
            UpdateSyncProblem();
        }
    }

    private void UpdateSyncProblem()
    {
        _viewModel.HasSelectedAccount = _selectedAccount is not null;
        _viewModel.ProblemAccount = _viewModel.Accounts.FirstOrDefault(account =>
            (_selectedAccount is null || account.Id == _selectedAccount) &&
            account.Status is ConnectionStatus.Failed or ConnectionStatus.NeedsAuthorization);
        var problem = _viewModel.ProblemAccount;
        _viewModel.ProblemRetryText = _syncing ? "正在同步，请稍候…" : problem is null ? "" :
            problem.Status == ConnectionStatus.NeedsAuthorization ? "更新授权后可重新同步" :
            _retryMap.TryGetValue(problem.Id, out var retry) ? $"下次自动重试：{retry.RetryAt.LocalDateTime:HH:mm:ss} · 也可立即重试" : "点击立即重试以获取最新失败原因";
        var searching = !string.IsNullOrWhiteSpace(_viewModel.SearchText);
        _viewModel.EmptyTitle = searching ? "没有找到相关邮件" : _syncing ? "正在同步邮件" :
            problem is not null ? "邮件尚未同步成功" : _viewModel.Accounts.Count == 0 ? "连接你的第一个邮箱" : "收件箱还是空的";
        _viewModel.EmptyDescription = searching ? "换个关键词试试，仅搜索本地已同步内容。" : _syncing ? "首次同步可能需要一些时间，请稍候。" :
            problem is not null ? "查看上方失败原因，处理后重新同步。" : _viewModel.Accounts.Count == 0 ? "支持QQ、网易和Gmail，多账号邮件集中查看。" : "同步最近90天的收件邮件，点击同步可再次检查。";
        if (_starredOnly && !searching && !_syncing && _viewModel.Accounts.Count > 0)
        {
            _viewModel.EmptyTitle = "还没有收藏的邮件";
            _viewModel.EmptyDescription = "点击邮件右侧的星标，将已同步的邮件加入收藏夹。";
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await SyncAllAsync(force: true, accountId: _selectedAccount);
    private async void OnRetryProblem(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ProblemAccount is Account account)
            await SyncAllAsync(force: true, accountId: account.Id);
    }
    private void OnFocusSyncProblem(object sender, RoutedEventArgs e)
    {
        var account = _viewModel.Accounts.FirstOrDefault(item => item.Status is ConnectionStatus.Failed or ConnectionStatus.NeedsAuthorization);
        if (account is not null) AccountsList.SelectedItem = account;
    }
    private void OnAuthorizeProblem(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ProblemAccount is not Account account) return;
        AccountsList.SelectedItem = account;
        OnReauthorize(sender, e);
    }

    private async void OnUnifiedInbox(object sender, RoutedEventArgs e) => await SelectUnifiedFolderAsync(false);

    private async void OnFavorites(object sender, RoutedEventArgs e) => await SelectUnifiedFolderAsync(true);

    private async Task SelectUnifiedFolderAsync(bool starredOnly)
    {
        _listScrollOffset = 0;
        ShowInbox();
        _selectedAccount = null;
        _starredOnly = starredOnly;
        _loadingAccounts = true;
        try { AccountsList.SelectedItem = null; }
        finally { _loadingAccounts = false; }
        UpdateNavigationSelection();
        await LoadMessagesAsync();
    }
    private async void OnAccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccounts) return;
        _starredOnly = false;
        _listScrollOffset = 0;
        ShowInbox();
        _selectedAccount = (AccountsList.SelectedItem as Account)?.Id;
        UpdateNavigationSelection();
        if (IsLoaded) await LoadMessagesAsync();
    }

    private void UpdateNavigationSelection()
    {
        AllInboxButton.Background = _selectedAccount is null && !_starredOnly
            ? (System.Windows.Media.Brush)FindResource("SelectedBrush")
            : System.Windows.Media.Brushes.Transparent;
        FavoritesButton.Background = _starredOnly
            ? (System.Windows.Media.Brush)FindResource("SelectedBrush")
            : System.Windows.Media.Brushes.Transparent;
    }

    private async void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        _bodyLoadCancellation?.Cancel();
        HtmlBody.Visibility = Visibility.Collapsed;
        PlainBody.Visibility = Visibility.Visible;
        RemoteImagesButton.Visibility = Visibility.Collapsed;
        _viewModel.IsBodyLoading = false;
        if (MessageList.SelectedItem is not MessageRow row)
        {
            _viewModel.ShowMessage(null);
            ReadButton.IsEnabled = StarButton.IsEnabled = false;
            return;
        }
        if (_viewModel.IsListVisible) _listScrollOffset = FindMessageScrollViewer(MessageList)?.VerticalOffset ?? 0;
        _viewModel.ShowMessage(row.Message);
        ReadButton.IsEnabled = StarButton.IsEnabled = true;
        ReadButton.Content = row.Message.IsRead ? "标为未读" : "标为已读";
        StarButton.Content = row.Message.IsStarred ? "取消星标" : "星标";
        _allowRemoteImages = _viewModel.AutoLoadRemoteImages;
        using var bodyLoadCancellation = new CancellationTokenSource();
        _bodyLoadCancellation = bodyLoadCancellation;
        _viewModel.IsBodyLoading = !row.HasCachedBody;
        try
        {
            if (!row.HasCachedBody)
            {
                var account = _viewModel.Accounts.FirstOrDefault(item => item.Id == row.Message.AccountId);
                if (account is null || !_providerMap.TryGetValue(account.Provider, out var provider))
                {
                    _viewModel.StatusText = "此账号尚未完成授权，暂时无法加载正文";
                    return;
                }
                var secret = _vault.Read(account.Id);
                if (secret is null)
                {
                    _viewModel.StatusText = "此账号需要更新授权，暂时无法加载正文";
                    return;
                }
                var fullMessage = await provider.GetMessageAsync(account, secret, row.Message.ServerId, bodyLoadCancellation.Token);
                bodyLoadCancellation.Token.ThrowIfCancellationRequested();
                row.CacheBody(fullMessage);
                await _store.UpsertMessagesAsync([row.Message], bodyLoadCancellation.Token);
            }
            if (bodyLoadCancellation.IsCancellationRequested || !ReferenceEquals(MessageList.SelectedItem, row)) return;
            _viewModel.ShowMessage(row.Message);
            await ShowBodyAsync(row.Message, bodyLoadCancellation.Token);
            if (!bodyLoadCancellation.IsCancellationRequested && ReferenceEquals(MessageList.SelectedItem, row))
            {
                _viewModel.IsBodyLoading = false;
                await ChangeMessageFlagAsync(row, readFlag: true, desiredValue: true);
            }
        }
        catch (OperationCanceledException) when (bodyLoadCancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!bodyLoadCancellation.IsCancellationRequested && ReferenceEquals(MessageList.SelectedItem, row))
            {
                _viewModel.StatusText = "正文暂时无法加载，请重新选择邮件重试";
                if (string.IsNullOrWhiteSpace(_viewModel.SelectedBody))
                    _viewModel.SelectedBody = "正文暂时无法加载，请重新选择邮件重试。";
            }
        }
        finally
        {
            if (ReferenceEquals(_bodyLoadCancellation, bodyLoadCancellation))
            {
                _bodyLoadCancellation = null;
                _viewModel.IsBodyLoading = false;
            }
        }
    }

    private static ScrollViewer? FindMessageScrollViewer(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindMessageScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void OnBackToList(object sender, RoutedEventArgs e) => ShowInbox();

    private void ShowInbox()
    {
        _bodyLoadCancellation?.Cancel();
        MessageList.SelectedItem = null;
        _viewModel.ShowMessage(null);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FindMessageScrollViewer(MessageList)?.ScrollToVerticalOffset(_listScrollOffset));
    }

    private void OnPreviousMessage(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AdjacentMessage(-1) is MessageRow row) MessageList.SelectedItem = row;
    }

    private void OnNextMessage(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AdjacentMessage(1) is MessageRow row) MessageList.SelectedItem = row;
    }

    private async Task ChangeMessageFlagAsync(MessageRow row, bool readFlag, bool? desiredValue = null)
    {
        MailMessage updated;
        await _flagWriteLock.WaitAsync();
        try
        {
            var old = row.Message;
            var value = desiredValue ?? (readFlag ? !old.IsRead : !old.IsStarred);
            if ((readFlag ? old.IsRead : old.IsStarred) == value) return;
            updated = readFlag ? old with { IsRead = value } : old with { IsStarred = value };
            var kind = readFlag ? (value ? PendingActionKind.MarkRead : PendingActionKind.MarkUnread)
                : (value ? PendingActionKind.Star : PendingActionKind.Unstar);
            row.UpdateFlags(updated.IsRead, updated.IsStarred);
            try
            {
                await Task.Run(async () =>
                {
                    await _store.UpsertMessagesAsync([updated]);
                    await _store.EnqueueAsync(new PendingAction(Guid.NewGuid(), old.AccountId, old.ServerId, kind, DateTimeOffset.UtcNow));
                });
            }
            catch
            {
                row.UpdateFlags(old.IsRead, old.IsStarred);
                _viewModel.StatusText = "邮件状态未能保存，请重试";
                Notify(_viewModel.StatusText, "失败");
                return;
            }
            if (_starredOnly && !readFlag && !updated.IsStarred)
            {
                if (ReferenceEquals(MessageList.SelectedItem, row)) ShowInbox();
                _viewModel.Messages.Remove(row);
                UpdateSyncProblem();
            }
            _viewModel.RefreshMailCounts();
            if (ReferenceEquals(MessageList.SelectedItem, row))
            {
                ReadButton.Content = row.Message.IsRead ? "标为未读" : "标为已读";
                StarButton.Content = row.Message.IsStarred ? "取消星标" : "星标";
            }
            if (!readFlag) Notify(updated.IsStarred ? "已添加星标" : "已取消星标", "成功");
        }
        finally { _flagWriteLock.Release(); }
        try
        {
            var account = _viewModel.Accounts.FirstOrDefault(item => item.Id == updated.AccountId);
            if (account is null || !_providerMap.TryGetValue(account.Provider, out var provider)) return;
            var secret = _vault.Read(account.Id);
            if (secret is null) { Notify("邮件状态已保存在本机，更新授权后同步", "告警"); return; }
            await ProcessPendingAsync(account, provider, secret);
        }
        catch { Notify("邮件状态已保存在本机，恢复连接后同步", "告警"); }
    }

    private async Task ProcessPendingAsync(Account account, IMailProvider provider, string secret)
    {
        await _pendingWriteLock.WaitAsync();
        try
        {
            foreach (var action in await _store.GetPendingAsync(account.Id))
            {
                var read = action.Kind switch
                {
                    PendingActionKind.MarkRead => true,
                    PendingActionKind.MarkUnread => false,
                    _ => (bool?)null
                };
                var starred = action.Kind switch
                {
                    PendingActionKind.Star => true,
                    PendingActionKind.Unstar => false,
                    _ => (bool?)null
                };
                await provider.SetFlagsAsync(account, secret, action.MessageServerId, read, starred, CancellationToken.None);
                await _store.CompletePendingAsync(action.Id);
            }
        }
        finally { _pendingWriteLock.Release(); }
    }

    private async Task ToggleFlagAsync(bool readFlag)
    {
        if (MessageList.SelectedItem is MessageRow row) await ChangeMessageFlagAsync(row, readFlag);
    }

    private async void OnToggleRead(object sender, RoutedEventArgs e) => await ToggleFlagAsync(true);
    private async void OnToggleStar(object sender, RoutedEventArgs e) => await ToggleFlagAsync(false);

    private async void OnRowToggleStar(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: MessageRow row })
            await ChangeMessageFlagAsync(row, readFlag: false);
    }

    private async void OnDownloadAttachments(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsDownloadingAttachments || MessageList.SelectedItem is not MessageRow row) return;
        var account = _viewModel.Accounts.FirstOrDefault(item => item.Id == row.Message.AccountId);
        if (account is null || !_providerMap.TryGetValue(account.Provider, out var provider)) return;
        var secret = _vault.Read(account.Id);
        if (secret is null) { Notify("账号需要更新授权，无法下载附件", "告警"); return; }
        _viewModel.IsDownloadingAttachments = true;
        Notify("正在检查邮件附件…", "进度");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (!row.HasAttachments)
            {
                Notify("这封邮件没有附件；正文图片请使用“保存内嵌图片”", "告警");
                return;
            }
            var attachmentList = await provider.GetAttachmentsAsync(account, secret, row.Message.ServerId, timeout.Token);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            if (attachmentList.Count == 0) { Notify("这封邮件没有可下载的附件；正文图片请使用“保存内嵌图片”", "告警"); return; }
            var downloadDirectory = _viewModel.DownloadDirectory;
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                using var picker = new Forms.FolderBrowserDialog { Description = "选择附件保存位置" };
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                downloadDirectory = picker.SelectedPath;
            }
            System.IO.Directory.CreateDirectory(downloadDirectory);
            var saved = 0;
            foreach (var attachment in attachmentList)
            {
                if (attachment.Size > 25L * 1024 * 1024)
                {
                    Notify($"附件{attachment.FileName}超过25MB，已跳过", "告警");
                    continue;
                }
                var output = MailRules.AvailableAttachmentPath(downloadDirectory, attachment.FileName);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                Notify($"正在下载{saved + 1}/{attachmentList.Count}：{attachment.FileName}", "进度");
                await provider.DownloadAttachmentAsync(account, secret, attachment, output, timeout.Token);
                saved++;
            }
            _viewModel.StatusText = $"已保存 {saved} 个附件";
            Notify($"已保存{saved}个附件至：{downloadDirectory}", saved > 0 ? "成功" : "告警");
        }
        catch (OperationCanceledException) { Notify("附件下载超时，请检查网络后重试", "失败"); }
        catch (Exception error)
        {
            Notify($"附件下载失败：{error.Message}", "失败");
        }
        finally { _viewModel.IsDownloadingAttachments = false; }
    }

    private async Task ShowBodyAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        RemoteImagesButton.Visibility = !_allowRemoteImages && !string.IsNullOrWhiteSpace(message.HtmlBody)
            ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(message.HtmlBody))
        {
            HtmlBody.Visibility = Visibility.Collapsed;
            PlainBody.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            _webEnvironment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: System.IO.Path.Combine(_dataDirectory, "WebView2"));
            await HtmlBody.EnsureCoreWebView2Async(_webEnvironment);
            HtmlBody.ZoomFactor = _readerSettings.FontScalePercent / 100d;
            if (cancellationToken.IsCancellationRequested || MessageList.SelectedItem is not MessageRow selected || selected.Message.Key != message.Key) return;
            var web = HtmlBody.CoreWebView2;
            web.Settings.IsScriptEnabled = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = true;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.WebResourceRequested -= OnWebResourceRequested;
            web.WebResourceRequested += OnWebResourceRequested;
            web.NavigationStarting -= OnWebNavigationStarting;
            web.NavigationStarting += OnWebNavigationStarting;
            web.NewWindowRequested -= OnNewWindowRequested;
            web.NewWindowRequested += OnNewWindowRequested;
            web.ContextMenuRequested -= OnWebContextMenuRequested;
            web.ContextMenuRequested += OnWebContextMenuRequested;
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            var imagePolicy = _allowRemoteImages ? "img-src data: https: http:;" : $"img-src data: https://{InlineImages.Host};";
            var html = """
                <!doctype html><html><head><meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none';
                """ + imagePolicy + """
                 style-src 'unsafe-inline'; font-src data:">
                <style>html,body{margin:0;background:#fff;color:#193447;font:14px/1.7 'Segoe UI Variable Text','Microsoft YaHei UI',sans-serif}body{overflow-wrap:anywhere}img{max-width:100%;height:auto}table{max-width:100%}</style>
                </head><body>
                """ + message.HtmlBody + "</body></html>";
            web.NavigateToString(html);
            PlainBody.Visibility = Visibility.Collapsed;
            HtmlBody.Visibility = Visibility.Visible;
        }
        catch
        {
            HtmlBody.Visibility = Visibility.Collapsed;
            PlainBody.Visibility = Visibility.Visible;
            _viewModel.StatusText = "HTML 阅读组件不可用，显示纯文本摘要";
        }
    }

    private void OnWebContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        e.Handled = true;
        var target = e.ContextMenuTarget;
        if (!target.HasSourceUri || !Uri.TryCreate(target.SourceUri, UriKind.Absolute, out var uri) ||
            uri.Host != InlineImages.Host || MessageList.SelectedItem is not MessageRow row) return;
        var accountId = row.Message.AccountId;
        var messageId = row.Message.ServerId;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var menu = new ContextMenu { PlacementTarget = HtmlBody, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            var saveItem = new MenuItem { Header = "保存图片" };
            saveItem.Click += async (_, _) => await SaveInlineImageAsync(uri, accountId, messageId);
            menu.Items.Add(saveItem);
            menu.IsOpen = true;
        });
    }

    private async void OnSaveInlineImages(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageRow row) return;
        var imageList = InlineImages.GetImageList(row.Message.HtmlBody);
        if (imageList.Count == 0)
        {
            Notify("这封邮件没有可保存的内嵌图片", "告警");
            return;
        }
        if (imageList.Count == 1)
        {
            await SaveInlineImageAsync(imageList[0], row.Message.AccountId, row.Message.ServerId);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = SaveImagesButton };
        for (var index = 0; index < imageList.Count; index++)
        {
            var uri = imageList[index];
            var item = new MenuItem { Header = $"保存图片{index + 1}" };
            item.Click += async (_, _) => await SaveInlineImageAsync(uri, row.Message.AccountId, row.Message.ServerId);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async Task SaveInlineImageAsync(Uri uri, Guid accountId, string messageId)
    {
        if (_savingImage) return;
        _savingImage = true;
        try
        {
            var downloadDirectory = _viewModel.DownloadDirectory;
            if (string.IsNullOrWhiteSpace(downloadDirectory))
            {
                using var picker = new Forms.FolderBrowserDialog { Description = "选择图片保存位置" };
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                downloadDirectory = picker.SelectedPath;
            }
            Notify("正在保存图片…", "进度");
            var path = await InlineImages.SaveAsync(uri, accountId, messageId, InlineImages.CacheDirectory,
                downloadDirectory, CancellationToken.None);
            Notify($"图片已保存（已有相同文件时不重复保存）：{path}", "成功");
            _viewModel.FooterText = $"图片已保存至：{path}";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            Notify($"图片保存失败：{error.Message}", "失败");
        }
        finally { _savingImage = false; }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Host == InlineImages.Host)
        {
            var contentType = "application/octet-stream";
            var row = MessageList.SelectedItem as MessageRow;
            var stream = row is not null && e.ResourceContext == CoreWebView2WebResourceContext.Image
                ? InlineImages.Open(uri, row.Message.AccountId, row.Message.ServerId, InlineImages.CacheDirectory, out contentType) : null;
            e.Response = HtmlBody.CoreWebView2.Environment.CreateWebResourceResponse(stream, stream is null ? 404 : 200,
                stream is null ? "Not Found" : "OK", $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
            return;
        }
        if (e.Request.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        if (_allowRemoteImages && e.ResourceContext == CoreWebView2WebResourceContext.Image &&
            (e.Request.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
             e.Request.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase))) return;
        e.Response = HtmlBody.CoreWebView2.Environment.CreateWebResourceResponse(null, 403, "已阻止远程资源", "Content-Type: text/plain");
    }

    private static void OnWebNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            e.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

    private async void OnShowRemoteImages(object sender, RoutedEventArgs e)
    {
        _allowRemoteImages = true;
        await ReloadSelectedBodyAsync();
    }

    private void ApplyReaderSettings(ReaderSettings settings)
    {
        _readerSettings = settings;
        _viewModel.AutoLoadRemoteImages = settings.AutoLoadRemoteImages;
        _viewModel.DownloadDirectory = settings.DownloadDirectory;
        App.ApplyFontScale(settings.FontScalePercent);
        HtmlBody.ZoomFactor = settings.FontScalePercent / 100d;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_readerSettings, _readerSettingsPath) { Owner = this };
        window.SettingsChanged += async (_, _) =>
        {
            var reloadBody = _readerSettings.AutoLoadRemoteImages != window.Settings.AutoLoadRemoteImages;
            ApplyReaderSettings(window.Settings);
            if (!reloadBody) return;
            _allowRemoteImages = _viewModel.AutoLoadRemoteImages;
            await ReloadSelectedBodyAsync();
        };
        window.ShowDialog();
    }

    private async Task ReloadSelectedBodyAsync()
    {
        if (MessageList.SelectedItem is not MessageRow row) return;
        var cached = (await _store.GetMessagesAsync(row.Message.AccountId, "INBOX", null, 1000))
            .FirstOrDefault(item => item.ServerId == row.Message.ServerId);
        await ShowBodyAsync(cached ?? row.Message);
    }

    private async void OnAddAccount(object sender, RoutedEventArgs e)
    {
        var dialog = new AccountDialog(_googleClientId, !string.IsNullOrWhiteSpace(_googleClientSecret)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var account = dialog.ResultAccount;
        var secret = dialog.Secret;
        if (dialog.SelectedProvider == ProviderKind.Gmail)
        {
            try
            {
                var clientId = dialog.GoogleClientId!;
                var clientSecret = dialog.GoogleClientSecret ?? _googleClientSecret!;
                var gmail = new GmailProvider(clientId, clientSecret, _vault);
                _viewModel.StatusText = "正在等待浏览器授权";
                var authorization = await gmail.AuthorizeAsync(CancellationToken.None);
                if (_viewModel.Accounts.Any(existing => existing.Provider == ProviderKind.Gmail &&
                    string.Equals(existing.Address, authorization.Email, StringComparison.OrdinalIgnoreCase)))
                {
                    _viewModel.StatusText = "账号已存在";
                    MessageBox.Show(this, "这个 Gmail 账号已添加。若需要更新授权，请选中账号后点击“重新授权”。", "OneBox");
                    return;
                }
                account = new Account(Guid.NewGuid(), authorization.Email, authorization.Email, ProviderKind.Gmail);
                secret = authorization.RefreshToken;
                System.IO.Directory.CreateDirectory(_dataDirectory);
                await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(_dataDirectory, "google-client-id.txt"), clientId);
                _vault.SaveGoogleClientSecret(clientId, clientSecret);
                _googleClientId = clientId;
                _googleClientSecret = clientSecret;
                _providerMap[ProviderKind.Gmail] = gmail;
            }
            catch (Exception error)
            {
                _viewModel.StatusText = "Gmail授权失败";
                MessageBox.Show(this, $"Gmail授权失败：{error.Message}", "OneBox");
                return;
            }
        }
        if (account is null) return;
        if (!_providerMap.TryGetValue(account.Provider, out var provider)) return;
        _viewModel.StatusText = "正在验证收信和发信";
        var check = await provider.TestConnectionAsync(account, secret, CancellationToken.None);
        if (!check.ReceivingSucceeded || !check.SendingSucceeded)
        {
            _viewModel.StatusText = "连接验证失败";
            MessageBox.Show(this, $"收信：{check.ReceivingError ?? "正常"}\n发信：{check.SendingError ?? "正常"}", "连接验证", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _vault.Save(account.Id, secret);
        try { await _store.UpsertAccountAsync(account with { Status = ConnectionStatus.Online }); }
        catch { _vault.Delete(account.Id); throw; }
        await LoadAccountsAsync();
        await SyncAllAsync();
    }

    private async void OnRemoveAccount(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not Account account)
        {
            MessageBox.Show(this, "请先选择要移除的账号", "OneBox");
            return;
        }
        if (_syncing)
        {
            MessageBox.Show(this, "正在同步，请完成后再移除账号", "OneBox");
            return;
        }
        if (MessageBox.Show(this, $"移除账号 {account.Address}？\n本机缓存和凭据会清除，邮箱服务器上的邮件不会删除。",
                "确认移除账号", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _pollTimer.Stop();
        _retryTimer.Stop();
        try
        {
            _vault.Delete(account.Id);
            await _store.RemoveAccountAsync(account.Id);
            try { await Task.Run(() => InlineImages.DeleteAccount(account.Id, InlineImages.CacheDirectory)); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"账号已移除，但本地图片缓存未能完全清理：{error.Message}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _retryMap.Remove(account.Id);
            _selectedAccount = null;
            await LoadAccountsAsync();
            await LoadMessagesAsync();
            _viewModel.ShowMessage(null);
            _viewModel.StatusText = "账号已移除";
        }
        finally { _pollTimer.Start(); _retryTimer.Start(); }
    }

    private async void OnReauthorize(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (AccountsList.SelectedItem is not Account account)
        {
            MessageBox.Show(this, "请先选择账号", "OneBox");
            return;
        }
        string secret;
        if (account.Provider == ProviderKind.Gmail)
        {
            try
            {
                var configured = _providerMap.TryGetValue(ProviderKind.Gmail, out var existingProvider) &&
                    existingProvider is GmailProvider;
                var clientId = _googleClientId;
                var clientSecret = _googleClientSecret;
                if (!configured)
                {
                    var setup = new AccountDialog(clientId, !string.IsNullOrWhiteSpace(clientSecret))
                        { Owner = this, Title = "补充 Google 授权配置" };
                    setup.SelectGmailOnly();
                    if (setup.ShowDialog() != true) return;
                    clientId = setup.GoogleClientId;
                    clientSecret = setup.GoogleClientSecret ?? clientSecret;
                }
                var gmail = configured ? (GmailProvider)existingProvider! :
                    new GmailProvider(clientId!, clientSecret!, _vault);
                _viewModel.StatusText = "正在等待浏览器授权";
                var authorization = await gmail.AuthorizeAsync(CancellationToken.None);
                if (!string.Equals(authorization.Email, account.Address, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, "授权的邮箱与所选账号不一致", "OneBox");
                    return;
                }
                secret = authorization.RefreshToken;
                if (!configured)
                {
                    System.IO.Directory.CreateDirectory(_dataDirectory);
                    await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(_dataDirectory, "google-client-id.txt"), clientId);
                    _vault.SaveGoogleClientSecret(clientId!, clientSecret!);
                    _googleClientId = clientId;
                    _googleClientSecret = clientSecret;
                    _providerMap[ProviderKind.Gmail] = gmail;
                }
            }
            catch (Exception error)
            {
                _viewModel.StatusText = "Gmail重新授权失败";
                MessageBox.Show(this, $"重新授权失败：{error.Message}", "OneBox");
                return;
            }
        }
        else
        {
            if (!_providerMap.TryGetValue(account.Provider, out var provider)) return;
            var dialog = new AccountDialog(account) { Owner = this, Title = "重新填写客户端授权码" };
            if (dialog.ShowDialog() != true) return;
            secret = dialog.Secret;
            var check = await provider.TestConnectionAsync(account, secret, CancellationToken.None);
            if (!check.ReceivingSucceeded || !check.SendingSucceeded)
            {
                MessageBox.Show(this, $"收信：{check.ReceivingError ?? "正常"}\n发信：{check.SendingError ?? "正常"}", "验证失败");
                return;
            }
        }
        _vault.Save(account.Id, secret);
        _retryMap.Remove(account.Id);
        await _store.UpsertAccountAsync(account with { Status = ConnectionStatus.Online, LastError = null });
        await LoadAccountsAsync();
        await SyncAllAsync(force: true);
    }

    private async void OnCompose(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Accounts.Count == 0) { MessageBox.Show(this, "请先添加邮箱账号", "OneBox"); return; }
        var dialog = new ComposeDialog(_viewModel.Accounts.ToArray(), _selectedAccount) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var outgoing = dialog.Message!;
        var account = _viewModel.Accounts.First(item => item.Id == outgoing.AccountId);
        if (!_providerMap.TryGetValue(account.Provider, out var provider)) return;
        var secret = _vault.Read(account.Id);
        if (secret is null) { MessageBox.Show(this, "账号需要重新授权", "OneBox"); return; }
        try
        {
            _viewModel.StatusText = "发送中";
            await provider.SendAsync(account, secret, outgoing, CancellationToken.None);
            _viewModel.StatusText = "已发送";
        }
        catch (Exception error)
        {
            _viewModel.StatusText = "发送结果待确认";
            MessageBox.Show(this, $"发送结果无法确认，请先检查邮箱的已发送文件夹，避免重复发送。\n{error.Message}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; Hide(); return; }
        _pollTimer.Stop();
        _searchTimer.Stop();
        _retryTimer.Stop();
        _bodyLoadCancellation?.Cancel();
        _noticeTimer.Stop();
        _tray.Visible = false;
        _noticePopupCloseTimer.Stop();
        NoticeHistoryPopup.IsOpen = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
    }
}
