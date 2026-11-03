using System.Diagnostics;
using System.IO;
using System.Net.Mail;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OneBox.Core;
using OneBox.Providers;
using MessageBox = System.Windows.MessageBox;

namespace OneBox.App;

public partial class AccountDialog : Window
{
    private readonly string? _configuredGoogleClientId;
    private readonly bool _hasGoogleClientSecret;

    public Account? ResultAccount { get; private set; }
    public string Secret { get; private set; } = "";
    public string? GoogleClientId { get; private set; }
    public string? GoogleClientSecret { get; private set; }
    public ProviderKind SelectedProvider => ProviderBox.SelectedIndex switch
    {
        0 => ProviderKind.Qq,
        1 => ProviderKind.NetEase,
        _ => ProviderKind.Gmail
    };

    public AccountDialog(string? googleClientId = null, bool hasGoogleClientSecret = false)
    {
        _configuredGoogleClientId = googleClientId;
        _hasGoogleClientSecret = hasGoogleClientSecret;
        InitializeComponent();
        ClientIdBox.Text = googleClientId ?? "";
        UpdateProviderUi();
    }

    public AccountDialog(Account existing) : this()
    {
        ProviderBox.SelectedIndex = existing.Provider switch
        {
            ProviderKind.Qq => 0,
            ProviderKind.NetEase => 1,
            _ => 2
        };
        ProviderBox.IsEnabled = false;
        AddressBox.Text = existing.Address;
        AddressBox.IsReadOnly = true;
        NameBox.Text = existing.DisplayName;
        NameBox.IsReadOnly = true;
    }

    public void SelectGmailOnly()
    {
        ProviderBox.SelectedIndex = 2;
        ProviderBox.IsEnabled = false;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderUi();

    private void UpdateProviderUi()
    {
        if (GmailPanel is null || StandardFieldsPanel is null || HelpText is null || AcceptButton is null) return;
        var gmail = SelectedProvider == ProviderKind.Gmail;
        GmailPanel.Visibility = gmail ? Visibility.Visible : Visibility.Collapsed;
        StandardFieldsPanel.Visibility = gmail ? Visibility.Collapsed : Visibility.Visible;
        GoogleSetupPanel.Visibility = gmail && (string.IsNullOrWhiteSpace(_configuredGoogleClientId) || !_hasGoogleClientSecret)
            ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Content = gmail ? "在浏览器中登录" : "验证并添加";
        HelpText.Text = SelectedProvider == ProviderKind.Qq
            ? "请先在 QQ 邮箱网页端开启 IMAP/SMTP。这里填写客户端授权码，不是邮箱登录密码。"
            : "请先在网易邮箱网页端开启 IMAP/SMTP。这里填写客户端授权码，不是邮箱登录密码。";
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var kind = SelectedProvider;
        if (kind == ProviderKind.Gmail)
        {
            var clientId = (GoogleSetupPanel.IsVisible ? ClientIdBox.Text : _configuredGoogleClientId ?? "").Trim();
            if (clientId.Length == 0 || clientId.Any(char.IsWhiteSpace) ||
                !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "请先填写有效的 Google 桌面 OAuth 客户端 ID", "OneBox");
                return;
            }
            var clientSecret = ClientSecretBox.Password.Trim();
            if (clientSecret.Length == 0 && (!_hasGoogleClientSecret ||
                !string.Equals(clientId, _configuredGoogleClientId, StringComparison.Ordinal)))
            {
                MessageBox.Show(this, "请导入桌面客户端 JSON，或填写与客户端 ID 对应的密钥", "OneBox");
                return;
            }
            GoogleClientId = clientId;
            GoogleClientSecret = clientSecret.Length == 0 ? null : clientSecret;
            DialogResult = true;
            return;
        }

        var address = AddressBox.Text.Trim();
        if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address)
        {
            MessageBox.Show(this, "请输入有效的邮箱地址", "OneBox");
            return;
        }
        try
        {
            ImapSmtpProvider.Servers(address);
            if ((kind == ProviderKind.Qq) != address.EndsWith("@qq.com", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException();
        }
        catch
        {
            MessageBox.Show(this, "邮箱类型与地址域名不匹配", "OneBox");
            return;
        }
        if (SecretBox.Password.Length == 0)
        {
            MessageBox.Show(this, "请输入客户端授权码", "OneBox");
            return;
        }
        ResultAccount = new Account(Guid.NewGuid(), address,
            string.IsNullOrWhiteSpace(NameBox.Text) ? address : NameBox.Text.Trim(), kind);
        Secret = SecretBox.Password;
        DialogResult = true;
    }

    private void OnOpenGmailApi(object sender, RoutedEventArgs e) => OpenBrowser("https://console.cloud.google.com/apis/library/gmail.googleapis.com");

    private void OnImportGoogleClient(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "选择 Google 桌面 OAuth 客户端 JSON",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 1024 * 1024)
                throw new InvalidDataException();
            using var document = JsonDocument.Parse(File.ReadAllText(picker.FileName));
            var installed = document.RootElement.GetProperty("installed");
            var id = installed.GetProperty("client_id").GetString();
            var secret = installed.GetProperty("client_secret").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret) ||
                !id.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException();
            ClientIdBox.Text = id;
            ClientSecretBox.Password = secret;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            MessageBox.Show(this, "这个文件不是有效的 Google 桌面 OAuth 客户端 JSON", "OneBox");
        }
    }

    private void OnOpenGoogleSetup(object sender, RoutedEventArgs e) => OpenBrowser("https://console.cloud.google.com/auth/clients");

    private void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "无法打开浏览器，请检查系统默认浏览器设置", "OneBox"); }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
