using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;
using Forms = System.Windows.Forms;

namespace OneBox.App;

public sealed class DesktopNotificationService : IDisposable
{
    private readonly Queue<(Account Account, MailMessage Message, bool Preview)> _pendingQueue = new();
    private readonly List<(MailNotificationWindow Window, Account Account, MailMessage Message, bool Preview)> _activeList = [];
    private readonly Dispatcher _dispatcher;
    private readonly Action<MailMessage> _open;
    private readonly Func<Account, MailMessage, CancellationToken, Task<string?>> _findCode;
    private readonly Func<MailMessage, Task> _markRead;
    private DesktopNotificationSettings _settings = new();
    private bool _disposed;
    private bool _clearing;
    private bool _arrangeQueued;

    public DesktopNotificationService(Dispatcher dispatcher, Action<MailMessage> open,
        Func<Account, MailMessage, CancellationToken, Task<string?>> findCode, Func<MailMessage, Task> markRead)
    {
        _dispatcher = dispatcher;
        _open = open;
        _findCode = findCode;
        _markRead = markRead;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void ApplySettings(DesktopNotificationSettings settings)
    {
        if (_settings == settings) return;
        _settings = settings;
        Refresh();
    }

    public void Enqueue(Account account, IEnumerable<MailMessage> messageList)
    {
        if (_disposed || !_settings.Enabled) return;
        foreach (var message in messageList) _pendingQueue.Enqueue((account, message, false));
        Pump();
    }

    public void ShowPreview()
    {
        if (_disposed) return;
        var account = new Account(Guid.Empty, "hello@onebox.app", "OneBox", ProviderKind.Qq);
        var message = new MailMessage(account.Id, "preview", "INBOX", "这是你的新邮件轻提示", "OneBox", account.Address,
            "", DateTimeOffset.Now, "悬停暂停关闭，点击通知可打开对应邮件。", false, false, false);
        _pendingQueue.Enqueue((account, message, true));
        Pump();
    }

    public void RemoveAccount(Guid accountId)
    {
        var retainedList = _pendingQueue.Where(item => item.Account.Id != accountId).ToArray();
        _pendingQueue.Clear();
        foreach (var item in retainedList) _pendingQueue.Enqueue(item);
        foreach (var item in _activeList.Where(item => item.Account.Id == accountId).ToArray()) item.Window.Close();
    }

    private Forms.Screen GetScreen() => Forms.Screen.AllScreens.FirstOrDefault(screen =>
        screen.DeviceName == _settings.ScreenDeviceName) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    private void Pump()
    {
        if (_disposed || _clearing) return;
        var area = GetScreen().WorkingArea;
        var maxVisible = Math.Clamp(_settings.MaxVisible, 1, 5);
        if (_activeList.Count > 0)
        {
            GetWindowRect(new WindowInteropHelper(_activeList[0].Window).Handle, out var bounds);
            maxVisible = Math.Min(maxVisible, Math.Max(1, (area.Height - 24) / Math.Max(1, bounds.Bottom - bounds.Top + 8)));
        }
        while (_pendingQueue.Count > 0 && _activeList.Count < maxVisible)
        {
            var item = _pendingQueue.Dequeue();
            var window = new MailNotificationWindow(item.Account, item.Message, _settings,
                () => { if (!item.Preview) _open(item.Message); });
            window.Closed += (_, _) => { _activeList.RemoveAll(active => active.Window == window); Pump(); };
            _activeList.Add((window, item.Account, item.Message, item.Preview));
            window.SourceInitialized += (_, _) => RequestArrange();
            window.SizeChanged += (_, _) => RequestArrange();
            window.ContentRendered += (_, _) => RequestArrange();
            window.DpiChanged += (_, _) => RequestArrange();
            var checkCode = !item.Preview && _settings.ShowContent;
            var waitForCode = checkCode && MailVerificationCode.IsLikelyVerificationMail(item.Message);
            if (waitForCode) window.BeginVerificationCheck();
            window.Show();
            if (checkCode) _ = ShowVerificationCodeAsync(window, item.Account, item.Message);
            Arrange();
            // 首张卡片创建后才能获得当前DPI下的实际窗口高度。
            GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
            maxVisible = Math.Min(maxVisible, Math.Max(1, (area.Height - 24) / Math.Max(1, bounds.Bottom - bounds.Top + 8)));
        }
        Arrange();
    }

    private async Task ShowVerificationCodeAsync(MailNotificationWindow window, Account account, MailMessage message)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));
        void Cancel(object? sender, EventArgs e) => cancellation.Cancel();
        window.Closed += Cancel;
        string? code = null;
        try
        {
            code = await _findCode(account, message, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            window.Closed -= Cancel;
            window.CompleteVerificationCheck(cancellation.IsCancellationRequested ? null : code, () => _markRead(message));
        }
    }

    private void Arrange()
    {
        var area = GetScreen().WorkingArea;
        var top = _settings.Corner is NotificationCorner.TopLeft or NotificationCorner.TopRight;
        var left = _settings.Corner is NotificationCorner.TopLeft or NotificationCorner.BottomLeft;
        var offset = 12;
        foreach (var item in _activeList)
        {
            var window = item.Window;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || window.ActualWidth <= 0 || window.ActualHeight <= 0) continue;
            GetWindowRect(handle, out var bounds);
            // SizeToContent增高时，原生窗口矩形可能尚未更新；按完成布局后的WPF尺寸定位。
            var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice;
            var width = transform is { } matrix ? (int)Math.Ceiling(window.ActualWidth * matrix.M11) : bounds.Right - bounds.Left;
            var height = transform is { } heightMatrix ? (int)Math.Ceiling(window.ActualHeight * heightMatrix.M22) : bounds.Bottom - bounds.Top;
            SetWindowPos(handle, new IntPtr(-1), left ? area.Left + 12 : area.Right - width - 12,
                top ? area.Top + offset : area.Bottom - height - offset, 0, 0, 0x0010 | 0x0001);
            offset += height + 8;
        }
    }

    private void RequestArrange()
    {
        if (_disposed || _arrangeQueued) return;
        _arrangeQueued = true;
        // 等SizeToContent和HWND尺寸更新完成，真实通知与测试通知复用相同的工作区边距。
        _dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _arrangeQueued = false;
            if (!_disposed) Arrange();
        }));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) Refresh(); }));

    private void Refresh()
    {
        var retainedList = _activeList.Select(item => (item.Account, item.Message, item.Preview)).Concat(_pendingQueue).ToArray();
        Clear();
        if (!_settings.Enabled || _disposed) return;
        foreach (var item in retainedList) _pendingQueue.Enqueue(item);
        Pump();
    }

    private void Clear()
    {
        _pendingQueue.Clear();
        _clearing = true;
        try { foreach (var item in _activeList.ToArray()) item.Window.Close(); }
        finally { _clearing = false; }
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowBounds { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out WindowBounds bounds);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
