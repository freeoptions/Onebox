using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OneBox.Core;
using OneBox.Storage;

namespace OneBox.App;

public partial class MailNotificationWindow : Window
{
    private readonly DispatcherTimer _timer = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action _open;
    private bool _dismissing;
    private bool _autoFading;
    private bool _checkingVerificationCode;
    private string? _verificationCode;
    private Func<Task>? _markRead;

    public MailNotificationWindow(Account account, MailMessage message, DesktopNotificationSettings settings, Action open)
    {
        InitializeComponent();
        _open = open;
        AccountText.Text = $"OneBox · {account.Address}";
        SenderText.Text = settings.ShowContent ? new MessageRow(message, account.Address).FromName : "收到新邮件";
        SubjectText.Text = settings.ShowContent ? (string.IsNullOrWhiteSpace(message.Subject) ? "（无主题）" : message.Subject) : "点击查看邮件";
        PreviewText.Text = settings.ShowContent ? new MessageRow(message, account.Address).Preview : "";
        PreviewText.Visibility = string.IsNullOrWhiteSpace(PreviewText.Text) ? Visibility.Collapsed : Visibility.Visible;
        OpenButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,
            $"{AccountText.Text}，{SenderText.Text}，{SubjectText.Text}");
        _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.DurationSeconds, 2, 30));
        _timer.Tick += (_, _) => Dismiss(automatic: true);
        MouseEnter += (_, _) =>
        {
            _timer.Stop();
            if (!_autoFading) return;
            var opacity = Opacity;
            BeginAnimation(OpacityProperty, null);
            _autoFading = false;
            _dismissing = false;
            BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, 1, TimeSpan.FromMilliseconds(120)));
        };
        MouseLeave += (_, _) => StartDismissTimer();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x00000080);
        };
        Loaded += (_, _) =>
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            }
            StartDismissTimer();
        };
        Closed += (_, _) =>
        {
            _dismissing = true;
            _timer.Stop();
            _closed.TrySetResult();
        };
    }

    private void StartDismissTimer()
    {
        if (!_dismissing && !_checkingVerificationCode && !IsMouseOver) _timer.Start();
    }

    internal void BeginVerificationCheck()
    {
        _checkingVerificationCode = true;
        _timer.Stop();
        VerificationStatusText.Text = "正在读取验证码…";
        VerificationStatusText.Visibility = Visibility.Visible;
    }

    internal void CompleteVerificationCheck(string? code, Func<Task> markRead)
    {
        if (_dismissing) return;
        if (code is not null)
        {
            _verificationCode = code;
            _markRead = markRead;
            VerificationStatusText.Visibility = Visibility.Collapsed;
            CopyCodeButton.Content = $"验证码 {code} · 复制";
            CopyCodeButton.Visibility = Visibility.Visible;
        }
        else if (_checkingVerificationCode)
        {
            VerificationStatusText.Text = "暂未识别到验证码，点击邮件查看";
        }
        else return;

        _checkingVerificationCode = false;
        _timer.Stop();
        StartDismissTimer();
    }

    private async void OnCopyCode(object sender, RoutedEventArgs e)
    {
        if (_dismissing || _verificationCode is not { } code || _markRead is not { } markRead) return;
        CopyCodeButton.IsEnabled = false;
        if (!await SelectionAutoCopy.CopyExplicitAsync(code))
        {
            CopyCodeButton.Content = $"验证码 {code} · 复制失败，重试";
            CopyCodeButton.IsEnabled = true;
            return;
        }
        await CompleteVerificationCopyAsync(markRead);
    }

    internal async Task CompleteVerificationCopyAsync(Func<Task> markRead)
    {
        Dismiss();
        await _closed.Task;
        await markRead();
    }

    private void Dismiss(bool openMessage = false, bool automatic = false)
    {
        if (_dismissing && (!_autoFading || automatic)) return;
        _dismissing = true;
        _autoFading = automatic;
        // 慢淡出期间仍可悬停恢复和点击，主动关闭时立即停止交互。
        IsHitTestVisible = automatic;
        _timer.Stop();
        if (!automatic || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, null);
            Close();
        }
        else
        {
            var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(4000))
            {
                // 参考视频约4秒：前段缓慢透出背景，后段逐渐加快，卡片保持原位。
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            animation.Completed += (_, _) => { if (!_closed.Task.IsCompleted) Close(); };
            BeginAnimation(OpacityProperty, animation);
        }
        if (openMessage) _open();
    }

    private void OnOpen(object sender, RoutedEventArgs e) => Dismiss(openMessage: true);
    private void OnDismiss(object sender, RoutedEventArgs e) => Dismiss();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
