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

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
