using System.IO;
using System.Windows;
using System.Windows.Controls;
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
