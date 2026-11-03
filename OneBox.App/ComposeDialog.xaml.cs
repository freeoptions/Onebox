using System.Net.Mail;
using System.Windows;
using Microsoft.Win32;
using OneBox.Core;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace OneBox.App;

public partial class ComposeDialog : Window
{
    private readonly List<string> _attachments = [];
    public OutgoingMessage? Message { get; private set; }

    public ComposeDialog(IReadOnlyList<Account> accounts, Guid? selectedAccount)
    {
        InitializeComponent();
        FromBox.ItemsSource = accounts;
        FromBox.SelectedItem = accounts.FirstOrDefault(account => account.Id == selectedAccount) ?? accounts[0];
    }

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "选择附件" };
        if (picker.ShowDialog(this) != true) return;
        foreach (var path in picker.FileNames)
        {
            if (new System.IO.FileInfo(path).Length > 25L * 1024 * 1024)
            {
                MessageBox.Show(this, "单个附件不能超过 25 MB", "OneBox");
                return;
            }
            if (!_attachments.Contains(path, StringComparer.OrdinalIgnoreCase)) _attachments.Add(path);
        }
        AttachmentLabel.Text = $"{_attachments.Count} 个附件";
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        var recipients = ToBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0 || recipients.Any(address => !MailAddress.TryCreate(address, out _)))
        {
            MessageBox.Show(this, "请填写有效的收件人地址", "OneBox");
            return;
        }
        if (FromBox.SelectedItem is not Account account) return;
        if (MessageBox.Show(this, $"发件人：{account.Address}\n收件人：{string.Join(", ", recipients)}\n附件：{_attachments.Count} 个\n\n确认发送？",
                "确认发送", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Message = new OutgoingMessage(account.Id, recipients, SubjectBox.Text.Trim(), BodyBox.Text, _attachments.ToArray());
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
