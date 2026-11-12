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
    private bool _legacyGoogleOAuth;
    private CancellationTokenSource? _validationCancellation;

    public Func<Account, string, CancellationToken, Task<ConnectionCheck>>? ValidateConnectionAsync { get; init; }

    public Account? ResultAccount { get; private set; }
    public string Secret { get; private set; } = "";
    public string? GoogleClientId { get; private set; }
    public string? GoogleClientSecret { get; private set; }
    public ProviderKind SelectedProvider => ProviderBox.SelectedIndex switch
    {
        0 => ProviderKind.Qq,
        1 => ProviderKind.NetEase,
        _ => _legacyGoogleOAuth ? ProviderKind.Gmail : ProviderKind.GmailImap
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
        _legacyGoogleOAuth = true;
        ProviderBox.SelectedIndex = 2;
        ProviderBox.IsEnabled = false;
        UpdateProviderUi();
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderUi();

    private void UpdateProviderUi()
    {
        if (GmailPanel is null || StandardFieldsPanel is null || HelpText is null || AcceptButton is null || SecretLabel is null || AppPasswordButton is null) return;
        var gmail = SelectedProvider == ProviderKind.Gmail;
        GmailPanel.Visibility = gmail ? Visibility.Visible : Visibility.Collapsed;
        StandardFieldsPanel.Visibility = gmail ? Visibility.Collapsed : Visibility.Visible;
        GoogleSetupPanel.Visibility = gmail && (string.IsNullOrWhiteSpace(_configuredGoogleClientId) || !_hasGoogleClientSecret)
            ? Visibility.Visible : Visibility.Collapsed;
        AcceptButton.Content = gmail ? "在浏览器中登录" : "验证并添加";
        var gmailImap = SelectedProvider == ProviderKind.GmailImap;
        SecretLabel.Text = gmailImap ? "应用专用密码" : "客户端授权码";
        AppPasswordButton.Visibility = gmailImap ? Visibility.Visible : Visibility.Collapsed;
        HelpText.Text = SelectedProvider switch
        {
            ProviderKind.Qq => "请先在QQ邮箱网页端开启IMAP/SMTP。这里填写客户端授权码，不是邮箱登录密码。",
            ProviderKind.GmailImap => "请先开启Google账号的两步验证，再生成16位应用专用密码填写到这里，不是Google登录密码。部分组织账号或启用高级保护的账号可能无法生成应用专用密码。",
            _ => "请先在网易邮箱网页端开启IMAP/SMTP。这里填写客户端授权码，不是邮箱登录密码。"
        };
    }

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_validationCancellation is not null) return;
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
            var domain = address.Split('@').Last().ToLowerInvariant();
            if (!(kind switch
                {
                    ProviderKind.Qq => domain == "qq.com",
                    ProviderKind.NetEase => domain is "163.com" or "126.com" or "yeah.net",
                    ProviderKind.GmailImap => domain is "gmail.com" or "googlemail.com",
                    _ => false
                }))
                throw new ArgumentException();
        }
        catch
        {
            MessageBox.Show(this, "邮箱类型与地址域名不匹配", "OneBox");
            return;
        }
        var secret = kind == ProviderKind.GmailImap
            ? string.Concat(SecretBox.Password.Where(character => !char.IsWhiteSpace(character)))
            : SecretBox.Password;
        if (secret.Length == 0)
        {
            MessageBox.Show(this, kind == ProviderKind.GmailImap ? "请输入应用专用密码" : "请输入客户端授权码", "OneBox");
            return;
        }
        if (kind == ProviderKind.GmailImap && secret.Length != 16)
        {
            MessageBox.Show(this, "请输入Google生成的16位应用专用密码", "OneBox");
            return;
        }
        var account = new Account(Guid.NewGuid(), address,
            string.IsNullOrWhiteSpace(NameBox.Text) ? address : NameBox.Text.Trim(), kind);
        if (ValidateConnectionAsync is not null)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            _validationCancellation = cancellation;
            AcceptButton.IsEnabled = ProviderBox.IsEnabled = StandardFieldsPanel.IsEnabled = false;
            AcceptButton.Content = "正在验证…";
            ValidationProgress.Visibility = Visibility.Visible;
            ValidationText.Text = "正在验证收信和发信连接，每项最多等待15秒。可点击取消。";
            try
            {
                var check = await ValidateConnectionAsync(account, secret, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!check.ReceivingSucceeded || !check.SendingSucceeded)
                {
                    ValidationText.Text = $"收信：{check.ReceivingError ?? "正常"}\n发信：{check.SendingError ?? "正常"}";
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                ValidationText.Text = "连接验证已取消或超时，请检查设置中的代理及网络后重试。";
                return;
            }
            catch (Exception error)
            {
                ValidationText.Text = SyncFailure.Describe(error, kind, "验证收信和发信", secret).DisplayText;
                return;
            }
            finally
            {
                _validationCancellation = null;
                AcceptButton.IsEnabled = StandardFieldsPanel.IsEnabled = true;
                ProviderBox.IsEnabled = !AddressBox.IsReadOnly;
                AcceptButton.Content = "验证并添加";
                ValidationProgress.Visibility = Visibility.Collapsed;
            }
        }
        ResultAccount = account;
        Secret = secret;
        DialogResult = true;
    }

    private void OnOpenAppPasswords(object sender, RoutedEventArgs e) => OpenBrowser("https://myaccount.google.com/apppasswords");

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

    protected override void OnClosed(EventArgs e)
    {
        _validationCancellation?.Cancel();
        base.OnClosed(e);
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _validationCancellation?.Cancel();
        DialogResult = false;
    }
}
