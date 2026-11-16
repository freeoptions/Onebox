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
    private readonly Action _open;
    private bool _dismissing;

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
        _timer.Tick += (_, _) => Dismiss();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) => { if (!_dismissing) _timer.Start(); };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000 | 0x00000080);
        };
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            if (!IsMouseOver) _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void Dismiss(bool openMessage = false)
    {
        if (_dismissing) return;
        _dismissing = true;
        _timer.Stop();
        var animation = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        animation.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, animation);
        if (openMessage) _open();
    }

    private void OnOpen(object sender, RoutedEventArgs e) => Dismiss(openMessage: true);
    private void OnDismiss(object sender, RoutedEventArgs e) => Dismiss();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
