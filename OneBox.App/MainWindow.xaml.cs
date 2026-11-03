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
    private readonly WindowsCredentialVault _vault = new();
    private readonly Dictionary<ProviderKind, IMailProvider> _providers = new()
    {
        [ProviderKind.Qq] = new ImapSmtpProvider(ProviderKind.Qq),
        [ProviderKind.NetEase] = new ImapSmtpProvider(ProviderKind.NetEase)
    };
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromHours(5) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Forms.NotifyIcon _tray;
    private CoreWebView2Environment? _webEnvironment;
    private string? _googleClientId;
    private string? _googleClientSecret;
    private bool _exiting;
    private bool _syncing;
    private bool _loadingAccounts;
    private bool _allowRemoteImages;
    private Guid? _selectedAccount;
    private readonly Dictionary<Guid, (int Failures, DateTimeOffset RetryAt)> _retry = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _dataDirectory = AppContext.BaseDirectory;
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
                    _providers[ProviderKind.Gmail] = new GmailProvider(clientId, _googleClientSecret, _vault);
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
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await LoadMessagesAsync(); };
        _viewModel.SearchChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
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
            await _store.InitializeAsync();
            await LoadAccountsAsync();
            await LoadMessagesAsync();
            _pollTimer.Start();
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
    }

    private async Task LoadMessagesAsync()
    {
        var selectedKey = (MessageList.SelectedItem as MessageRow)?.Message.Key;
        var messages = await _store.GetMessagesAsync(_selectedAccount, "INBOX", _viewModel.SearchText);
        _viewModel.ShowMessages(messages);
        if (selectedKey is not null)
            MessageList.SelectedItem = _viewModel.Messages.FirstOrDefault(row => row.Message.Key == selectedKey);
        _viewModel.CurrentFolderTitle = _selectedAccount is Guid id
            ? _viewModel.Accounts.FirstOrDefault(account => account.Id == id)?.DisplayName ?? "收件箱"
            : "所有收件箱";
    }

    private async Task SyncAllAsync(bool force = false)
    {
        if (_syncing) return;
        _syncing = true;
        _viewModel.StatusText = "同步中";
        try
        {
            var failures = 0;
            foreach (var account in _viewModel.Accounts.ToArray())
            {
                if (!force && _retry.TryGetValue(account.Id, out var retry) && retry.RetryAt > DateTimeOffset.UtcNow)
                { failures++; continue; }
                if (!_providers.TryGetValue(account.Provider, out var provider)) { failures++; continue; }
                var secret = _vault.Read(account.Id);
                if (secret is null)
                {
                    failures++;
                    await _store.UpsertAccountAsync(account with { Status = ConnectionStatus.NeedsAuthorization });
                    continue;
                }
                try
                {
                    await ProcessPendingAsync(account, provider, secret);
                    var cursor = await _store.GetCursorAsync(account.Id, "INBOX");
                    var messages = await provider.GetRecentInboxAsync(account, secret, cursor, DateTimeOffset.UtcNow.AddDays(-90), CancellationToken.None);
                    var validity = (provider as ImapSmtpProvider)?.CurrentUidValidity;
                    if (validity is not null && cursor?.UidValidity is not null && validity != cursor.UidValidity)
                        await _store.ResetFolderAsync(account.Id, "INBOX");
                    await _store.UpsertMessagesAsync(messages);
                    await _store.SaveCursorAsync(new SyncCursor(account.Id, "INBOX",
                        (provider as GmailProvider)?.LatestHistoryId, validity,
                        messages.Select(message => long.TryParse(message.ServerId, out var uid) ? uid : 0).DefaultIfEmpty(0).Max(), DateTimeOffset.UtcNow));
                    await _store.UpsertAccountAsync(account with { Status = ConnectionStatus.Online, LastError = null });
                    _retry.Remove(account.Id);
                }
                catch (Exception error)
                {
                    failures++;
                    var failureCount = _retry.TryGetValue(account.Id, out var previous) ? previous.Failures + 1 : 1;
                    var delay = TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, Math.Min(failureCount - 1, 4))));
                    _retry[account.Id] = (failureCount, DateTimeOffset.UtcNow + delay);
                    await _store.UpsertAccountAsync(account with
                    {
                        Status = error is UnauthorizedAccessException ? ConnectionStatus.NeedsAuthorization : ConnectionStatus.Failed,
                        LastError = error.GetType().Name
                    });
                }
            }
            await LoadAccountsAsync();
            await LoadMessagesAsync();
            _viewModel.StatusText = failures == 0 ? "已同步" : $"{failures} 个账号同步失败";
        }
        finally { _syncing = false; }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await SyncAllAsync(force: true);
    private async void OnUnifiedInbox(object sender, RoutedEventArgs e)
    {
        _selectedAccount = null;
        AccountsList.SelectedItem = null;
        UpdateNavigationSelection();
        await LoadMessagesAsync();
    }
    private async void OnAccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAccounts) return;
        _selectedAccount = (AccountsList.SelectedItem as Account)?.Id;
        UpdateNavigationSelection();
        if (IsLoaded) await LoadMessagesAsync();
    }

    private void UpdateNavigationSelection() =>
        AllInboxButton.Background = _selectedAccount is null
            ? (System.Windows.Media.Brush)FindResource("SelectedBrush")
            : System.Windows.Media.Brushes.Transparent;

    private async void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageRow row)
        {
            _viewModel.ShowMessage(null);
            ReadButton.IsEnabled = StarButton.IsEnabled = AttachmentButton.IsEnabled = false;
            RemoteImagesButton.Visibility = Visibility.Collapsed;
            HtmlBody.Visibility = Visibility.Collapsed;
            PlainBody.Visibility = Visibility.Visible;
            return;
        }
        MessageList.ScrollIntoView(row);
        var message = row.Message;
        if (message.TextBody is null && _providers.TryGetValue(
                _viewModel.Accounts.FirstOrDefault(account => account.Id == message.AccountId)?.Provider ?? ProviderKind.Gmail,
                out var provider))
        {
            var account = _viewModel.Accounts.First(account => account.Id == message.AccountId);
            var secret = _vault.Read(account.Id);
            if (secret is not null)
            {
                try
                {
                    var full = await provider.GetMessageAsync(account, secret, message.ServerId, CancellationToken.None);
                    message = full with { IsRead = message.IsRead, IsStarred = message.IsStarred };
                    await _store.UpsertMessagesAsync([message]);
                }
                catch { _viewModel.StatusText = "正文暂时无法加载，显示缓存摘要"; }
            }
        }
        if (MessageList.SelectedItem is not MessageRow selected || selected.Message.Key != row.Message.Key) return;
        _viewModel.ShowMessage(message);
        ReadButton.IsEnabled = StarButton.IsEnabled = AttachmentButton.IsEnabled = true;
        ReadButton.Content = message.IsRead ? "标为未读" : "标为已读";
        StarButton.Content = message.IsStarred ? "取消星标" : "星标";
        _allowRemoteImages = false;
        RemoteImagesButton.Visibility = string.IsNullOrWhiteSpace(message.HtmlBody) ? Visibility.Collapsed : Visibility.Visible;
        await ShowBodyAsync(message);
    }

    private async Task ProcessPendingAsync(Account account, IMailProvider provider, string secret)
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

    private async Task ToggleFlagAsync(bool readFlag)
    {
        if (MessageList.SelectedItem is not MessageRow row) return;
        var old = row.Message;
        var updated = readFlag ? old with { IsRead = !old.IsRead } : old with { IsStarred = !old.IsStarred };
        var kind = readFlag
            ? (updated.IsRead ? PendingActionKind.MarkRead : PendingActionKind.MarkUnread)
            : (updated.IsStarred ? PendingActionKind.Star : PendingActionKind.Unstar);
        await _store.UpsertMessagesAsync([updated]);
        await _store.EnqueueAsync(new PendingAction(Guid.NewGuid(), old.AccountId, old.ServerId, kind, DateTimeOffset.UtcNow));
        var index = _viewModel.Messages.IndexOf(row);
        _viewModel.Messages[index] = new MessageRow(updated, row.AccountLabel);
        MessageList.SelectedIndex = index;
        _viewModel.ShowMessage(updated);
        ReadButton.Content = updated.IsRead ? "标为未读" : "标为已读";
        StarButton.Content = updated.IsStarred ? "取消星标" : "星标";
        var account = _viewModel.Accounts.FirstOrDefault(item => item.Id == updated.AccountId);
        if (account is null || !_providers.TryGetValue(account.Provider, out var provider)) return;
        var secret = _vault.Read(account.Id);
        if (secret is null) return;
        try { await ProcessPendingAsync(account, provider, secret); }
        catch { _viewModel.StatusText = "离线更改已保存，恢复连接后同步"; }
    }

    private async void OnToggleRead(object sender, RoutedEventArgs e) => await ToggleFlagAsync(true);
    private async void OnToggleStar(object sender, RoutedEventArgs e) => await ToggleFlagAsync(false);

    private async void OnDownloadAttachments(object sender, RoutedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageRow row) return;
        var account = _viewModel.Accounts.FirstOrDefault(item => item.Id == row.Message.AccountId);
        if (account is null || !_providers.TryGetValue(account.Provider, out var provider)) return;
        var secret = _vault.Read(account.Id);
        if (secret is null) return;
        try
        {
            var attachments = await provider.GetAttachmentsAsync(account, secret, row.Message.ServerId, CancellationToken.None);
            if (attachments.Count == 0) { MessageBox.Show(this, "这封邮件没有可下载的附件", "OneBox"); return; }
            using var picker = new Forms.FolderBrowserDialog { Description = "选择附件保存位置" };
            if (picker.ShowDialog() != Forms.DialogResult.OK) return;
            var saved = 0;
            foreach (var attachment in attachments)
            {
                if (attachment.Size > 25L * 1024 * 1024)
                {
                    MessageBox.Show(this, $"附件 {attachment.FileName} 超过 25 MB，已跳过。", "OneBox");
                    continue;
                }
                var output = MailRules.AvailableAttachmentPath(picker.SelectedPath, attachment.FileName);
                await provider.DownloadAttachmentAsync(account, secret, attachment, output, CancellationToken.None);
                saved++;
            }
            _viewModel.StatusText = $"已保存 {saved} 个附件";
        }
        catch (Exception error)
        {
            MessageBox.Show(this, $"附件下载失败：{error.Message}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ShowBodyAsync(MailMessage message)
    {
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
            if (MessageList.SelectedItem is not MessageRow selected || selected.Message.Key != message.Key) return;
            var web = HtmlBody.CoreWebView2;
            web.Settings.IsScriptEnabled = false;
            web.Settings.IsWebMessageEnabled = false;
            web.Settings.AreDefaultContextMenusEnabled = false;
            web.Settings.AreDevToolsEnabled = false;
            web.Settings.IsStatusBarEnabled = false;
            web.WebResourceRequested -= OnWebResourceRequested;
            web.WebResourceRequested += OnWebResourceRequested;
            web.NavigationStarting -= OnWebNavigationStarting;
            web.NavigationStarting += OnWebNavigationStarting;
            web.NewWindowRequested -= OnNewWindowRequested;
            web.NewWindowRequested += OnNewWindowRequested;
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            var imagePolicy = _allowRemoteImages ? "img-src data: https: http:;" : "img-src data:;";
            var html = """
                <!doctype html><html><head><meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none';
                """ + imagePolicy + """
                 style-src 'unsafe-inline'; font-src data:">
                <style>html,body{margin:0;background:#fff;color:#193447;font:14px/1.7 'Microsoft YaHei UI',sans-serif}body{overflow-wrap:anywhere}img{max-width:100%;height:auto}table{max-width:100%}</style>
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

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
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
        if (MessageList.SelectedItem is not MessageRow row) return;
        _allowRemoteImages = true;
        RemoteImagesButton.Visibility = Visibility.Collapsed;
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
                _providers[ProviderKind.Gmail] = gmail;
            }
            catch (Exception error)
            {
                _viewModel.StatusText = "Gmail 授权失败";
                MessageBox.Show(this, $"Gmail 授权失败：{error.Message}", "OneBox");
                return;
            }
        }
        if (account is null) return;
        if (!_providers.TryGetValue(account.Provider, out var provider)) return;
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
        try
        {
            _vault.Delete(account.Id);
            await _store.RemoveAccountAsync(account.Id);
            _selectedAccount = null;
            await LoadAccountsAsync();
            await LoadMessagesAsync();
            _viewModel.ShowMessage(null);
            _viewModel.StatusText = "账号已移除";
        }
        finally { _pollTimer.Start(); }
    }

    private async void OnReauthorize(object sender, RoutedEventArgs e)
    {
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
                var configured = _providers.TryGetValue(ProviderKind.Gmail, out var existingProvider) &&
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
                    _providers[ProviderKind.Gmail] = gmail;
                }
            }
            catch (Exception error)
            {
                _viewModel.StatusText = "Gmail 重新授权失败";
                MessageBox.Show(this, $"重新授权失败：{error.Message}", "OneBox");
                return;
            }
        }
        else
        {
            if (!_providers.TryGetValue(account.Provider, out var provider)) return;
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
        _retry.Remove(account.Id);
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
        if (!_providers.TryGetValue(account.Provider, out var provider)) return;
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
        _tray.Visible = false;
        _tray.ContextMenuStrip?.Dispose();
        _tray.Dispose();
    }
}
