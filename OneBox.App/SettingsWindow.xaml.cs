using System.IO;
using System.Windows;
using System.Windows.Controls;
using OneBox.Core;
using OneBox.Storage;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace OneBox.App;

public partial class SettingsWindow : Window
{
    private readonly string _settingsPath;
    private bool _updatingControls;

    public ReaderSettings Settings { get; private set; }
    public event EventHandler? SettingsChanged;
    public event EventHandler? TestNotificationRequested;

    public SettingsWindow(ReaderSettings settings, string settingsPath)
    {
        Settings = settings;
        _settingsPath = settingsPath;
        InitializeComponent();
        RefreshControls();
    }

    private void RefreshControls()
    {
        _updatingControls = true;
        FontScaleBox.SelectedItem = Settings.FontScalePercent;
        RemoteImagesBox.IsChecked = Settings.AutoLoadRemoteImages;
        AutoCopySelectedTextBox.IsChecked = Settings.AutoCopySelectedText;
        ProxyModeBox.SelectedIndex = (int)Settings.Proxy.Mode;
        ProxyAddressBox.Text = Settings.Proxy.Address;
        CustomProxyPanel.Visibility = Settings.Proxy.Mode == ProxyMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        ProxyErrorText.Text = "";
        NotificationsBox.IsChecked = Settings.Notifications.Enabled;
        NotificationContentBox.IsChecked = Settings.Notifications.ShowContent;
        NotificationCornerBox.SelectedIndex = (int)Settings.Notifications.Corner;
        NotificationDurationBox.SelectedValue = Math.Clamp(Settings.Notifications.DurationSeconds, 2, 30).ToString();
        NotificationCountBox.SelectedValue = Math.Clamp(Settings.Notifications.MaxVisible, 1, 5).ToString();
        NotificationScreenBox.Items.Clear();
        NotificationScreenBox.Items.Add(new ComboBoxItem { Content = "主显示器（默认）", Tag = "" });
        foreach (var screen in Forms.Screen.AllScreens)
            NotificationScreenBox.Items.Add(new ComboBoxItem
            {
                Content = $"{screen.DeviceName} · {screen.Bounds.Width}×{screen.Bounds.Height}", Tag = screen.DeviceName
            });
        NotificationScreenBox.SelectedValue = Settings.Notifications.ScreenDeviceName;
        if (NotificationScreenBox.SelectedIndex < 0) NotificationScreenBox.SelectedIndex = 0;
        DownloadDirectoryLabel.Text = string.IsNullOrWhiteSpace(Settings.DownloadDirectory)
            ? "未设置，每次下载时选择" : Settings.DownloadDirectory;
        _updatingControls = false;
    }

    private void SaveSettings(ReaderSettings settings)
    {
        if (settings == Settings) return;
        try
        {
            settings.Save(_settingsPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RefreshControls();
            MessageBox.Show(this, $"设置未能保存：{error.Message}", "OneBox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Settings = settings;
        RefreshControls();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFontScaleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || FontScaleBox.SelectedItem is not int percent) return;
        SaveSettings(Settings with { FontScalePercent = percent });
    }

    private void OnRemoteImagesChanged(object sender, RoutedEventArgs e) =>
        SaveSettings(Settings with { AutoLoadRemoteImages = RemoteImagesBox.IsChecked == true });

    private void OnAutoCopySelectedTextChanged(object sender, RoutedEventArgs e) =>
        SaveSettings(Settings with { AutoCopySelectedText = AutoCopySelectedTextBox.IsChecked == true });

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls || CustomProxyPanel is null || ProxyErrorText is null) return;
        var mode = (ProxyMode)ProxyModeBox.SelectedIndex;
        CustomProxyPanel.Visibility = mode == ProxyMode.Custom ? Visibility.Visible : Visibility.Collapsed;
        ProxyErrorText.Text = "";
        if (mode == ProxyMode.Custom) return;
        SaveSettings(Settings with { Proxy = Settings.Proxy with { Mode = mode } });
    }

    private void OnApplyProxy(object sender, RoutedEventArgs e)
    {
        var proxy = new ProxySettings { Mode = ProxyMode.Custom, Address = ProxyAddressBox.Text.Trim() };
        try { proxy.ParseAddress(); }
        catch (ArgumentException error)
        {
            ProxyErrorText.Text = error.Message;
            return;
        }
        SaveSettings(Settings with { Proxy = proxy });
    }

    private void OnChooseDownloadDirectory(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "选择附件默认下载位置",
            SelectedPath = Settings.DownloadDirectory
        };
        if (picker.ShowDialog() == Forms.DialogResult.OK)
            SaveSettings(Settings with { DownloadDirectory = picker.SelectedPath });
    }

    private void OnClearDownloadDirectory(object sender, RoutedEventArgs e) =>
        SaveSettings(Settings with { DownloadDirectory = "" });

    private void OnNotificationChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingControls || NotificationScreenBox is null || NotificationCountBox is null ||
            NotificationDurationBox is null || NotificationCornerBox is null || NotificationContentBox is null) return;
        if (!int.TryParse(NotificationDurationBox.SelectedValue as string, out var seconds) ||
            !int.TryParse(NotificationCountBox.SelectedValue as string, out var count)) return;
        SaveSettings(Settings with
        {
            Notifications = Settings.Notifications with
            {
                Enabled = NotificationsBox.IsChecked == true,
                ShowContent = NotificationContentBox.IsChecked == true,
                Corner = (NotificationCorner)NotificationCornerBox.SelectedIndex,
                DurationSeconds = seconds,
                MaxVisible = count,
                ScreenDeviceName = NotificationScreenBox.SelectedValue as string ?? ""
            }
        });
    }

    private void OnTestNotification(object sender, RoutedEventArgs e) => TestNotificationRequested?.Invoke(this, EventArgs.Empty);

    private void OnSaveDeepSeekApiKey(object sender, RoutedEventArgs e)
    {
        var apiKey = DeepSeekApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            TranslationSettingsStatus.Text = "未填写新密钥，原密钥保持不变。";
            return;
        }
        try
        {
            new WindowsCredentialVault().SaveDeepSeekApiKey(apiKey);
            DeepSeekApiKeyBox.Clear();
            TranslationSettingsStatus.Text = "API Key已保存。可在邮件详情点击“翻译成中文”。";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or ArgumentException)
        {
            TranslationSettingsStatus.Text = "密钥保存失败，请检查密钥和Windows凭据管理器后重试。";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
