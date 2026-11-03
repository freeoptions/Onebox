using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OneBox.Core;

namespace OneBox.App;

public sealed class MessageRow(MailMessage message, string accountLabel)
{
    public MailMessage Message { get; } = message;
    public string AccountLabel { get; } = accountLabel;
    public string FromName
    {
        get
        {
            var name = Message.FromName?.Trim() ?? "";
            var addressStart = name.IndexOf('<');
            if (addressStart > 0) name = name[..addressStart].Trim().Trim('"');
            return string.IsNullOrWhiteSpace(name) ? Message.FromAddress : name;
        }
    }
    public string Subject => Message.Subject;
    public string Preview => Message.Preview;
    public DateTimeOffset ReceivedAt => Message.ReceivedAt;
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _searchText = "";
    private string _statusText = "离线";
    private string _currentFolderTitle = "所有收件箱";
    private string _mailCountText = "0 封邮件 · 搜索本地已同步内容";
    private string _selectedSubject = "选择一封邮件";
    private string _selectedSender = "";
    private string _selectedRecipient = "";
    private string _selectedBody = "";
    private string _footerText = "准备就绪";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SearchChanged;
    public ObservableCollection<Account> Accounts { get; } = [];
    public ObservableCollection<MessageRow> Messages { get; } = [];
    public bool HasMessages => Messages.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) SearchChanged?.Invoke(this, EventArgs.Empty); }
    }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string CurrentFolderTitle { get => _currentFolderTitle; set => Set(ref _currentFolderTitle, value); }
    public string MailCountText { get => _mailCountText; set => Set(ref _mailCountText, value); }
    public string SelectedSubject { get => _selectedSubject; set => Set(ref _selectedSubject, value); }
    public string SelectedSender { get => _selectedSender; set => Set(ref _selectedSender, value); }
    public string SelectedRecipient { get => _selectedRecipient; set => Set(ref _selectedRecipient, value); }
    public string SelectedBody { get => _selectedBody; set => Set(ref _selectedBody, value); }
    public string FooterText { get => _footerText; set => Set(ref _footerText, value); }

    public void ShowMessages(IEnumerable<MailMessage> messages)
    {
        var labels = Accounts.ToDictionary(account => account.Id, account => account.Address);
        Messages.Clear();
        foreach (var message in messages)
            Messages.Add(new MessageRow(message, labels.GetValueOrDefault(message.AccountId, "未知账号")));
        OnPropertyChanged(nameof(HasMessages));
        MailCountText = $"{Messages.Count} 封邮件 · 搜索本地已同步内容";
        FooterText = $"当前显示 {Messages.Count} 封邮件";
    }

    public void ShowMessage(MailMessage? message)
    {
        SelectedSubject = message?.Subject ?? "选择一封邮件";
        var fromName = message is null ? "" : new MessageRow(message, "").FromName;
        SelectedSender = message is null ? "" : $"发件人：{fromName}" +
            (string.Equals(fromName, message.FromAddress, StringComparison.OrdinalIgnoreCase) ? "" : $" <{message.FromAddress}>");
        SelectedRecipient = message is null ? "" : $"收件人：{message.ToAddresses}";
        SelectedBody = message?.TextBody ?? message?.Preview ?? "";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
