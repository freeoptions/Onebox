using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using OneBox.Core;
using OneBox.Providers;

namespace OneBox.App;

public sealed record NoticeEntry(string Text, string Kind, DateTimeOffset RecordedAt, string Details)
{
    public string TimeText => RecordedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class MessageRow(MailMessage message, string accountLabel) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MailMessage Message { get; private set; } = message;
    public bool HasCachedBody => (Message.TextBody is not null || Message.HtmlBody is not null)
        && !InlineImages.HasUnresolvedReferences(Message.HtmlBody) && !MailEncoding.NeedsReload(Message.HtmlBody);
    public void CacheBody(MailMessage fullMessage)
    {
        if (fullMessage.Key != Message.Key) throw new ArgumentException("正文与当前邮件不匹配", nameof(fullMessage));
        Message = Message with { TextBody = fullMessage.TextBody ?? "", HtmlBody = fullMessage.HtmlBody, Preview = fullMessage.Preview, HasAttachments = fullMessage.HasAttachments };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAttachments)));
    }
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
    public string Preview => Regex.Replace(Message.Preview, @"\s+", " ").Trim();
    public DateTime ReceivedAt => Message.ReceivedAt.LocalDateTime;
    public bool IsUnread => !Message.IsRead;
    public string ReadStatusText => IsUnread ? "未读" : "已读";
    public bool HasAttachments => Message.HasAttachments;
    public bool IsStarred => Message.IsStarred;
    public void UpdateFlags(bool isRead, bool isStarred)
    {
        Message = Message with { IsRead = isRead, IsStarred = isStarred };
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUnread)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReadStatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStarred)));
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _searchText = "";
    private string _statusText = "正在加载本地邮件…";
    private string _currentFolderTitle = "所有收件箱";
    private string _mailCountText = "0 封邮件 · 搜索本地已同步内容";
    private string _selectedSubject = "选择一封邮件";
    private string _selectedSender = "";
    private string _selectedRecipient = "";
    private string _selectedBody = "";
    private string _footerText = "准备就绪";
    private bool _isSyncing;
    private bool _hasSelectedMessage;
    private bool _hasSelectedAccount;
    private Account? _problemAccount;
    private string _problemRetryText = "";
    private string _emptyTitle = "收件箱还是空的";
    private string _emptyDescription = "添加邮箱账号，开始接收邮件。";
    private bool _autoLoadRemoteImages = true;
    private bool _isBodyLoading;
    private string _downloadDirectory = "";
    private string? _selectedMessageKey;
    private string _selectedDateText = "";
    private readonly List<NoticeEntry> _noticeList = [];
    private bool _isDownloadingAttachments;
    public string NoticeText => _noticeList.Count == 0 ? StatusText : _noticeList[^1].Text;
    public string NoticeKind => _noticeList.Count == 0 ? "状态" : _noticeList[^1].Kind;
    public string NoticeTimeText => _noticeList.Count == 0 ? "" : _noticeList[^1].TimeText;
    public IReadOnlyList<NoticeEntry> RecentNoticeList => _noticeList.AsEnumerable().Reverse().ToArray();
    public void Notify(string text, string kind, string details = "")
    {
        _noticeList.RemoveAll(item => item.Kind == "进度");
        _noticeList.Add(new NoticeEntry(text, kind, DateTimeOffset.Now, details));
        if (_noticeList.Count > 20) _noticeList.RemoveAt(0);
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(NoticeKind));
        OnPropertyChanged(nameof(NoticeTimeText));
        OnPropertyChanged(nameof(RecentNoticeList));
    }
    public bool IsDownloadingAttachments
    {
        get => _isDownloadingAttachments;
        set { if (Set(ref _isDownloadingAttachments, value)) { OnPropertyChanged(nameof(CanDownloadAttachments)); OnPropertyChanged(nameof(DownloadButtonText)); } }
    }
    public bool CanDownloadAttachments => HasSelectedMessage && !IsDownloadingAttachments;
    public string DownloadButtonText => IsDownloadingAttachments ? "正在下载…" : "下载附件";

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
    public string StatusText { get => _statusText; set { if (Set(ref _statusText, value)) OnPropertyChanged(nameof(NoticeText)); } }
    public string CurrentFolderTitle { get => _currentFolderTitle; set => Set(ref _currentFolderTitle, value); }
    public string MailCountText { get => _mailCountText; set => Set(ref _mailCountText, value); }
    public string SelectedSubject { get => _selectedSubject; set => Set(ref _selectedSubject, value); }
    public string SelectedSender { get => _selectedSender; set => Set(ref _selectedSender, value); }
    public string SelectedRecipient { get => _selectedRecipient; set => Set(ref _selectedRecipient, value); }
    public string SelectedBody { get => _selectedBody; set => Set(ref _selectedBody, value); }
    public string FooterText { get => _footerText; set => Set(ref _footerText, value); }
    public bool AutoLoadRemoteImages { get => _autoLoadRemoteImages; set => Set(ref _autoLoadRemoteImages, value); }
    public bool IsBodyLoading { get => _isBodyLoading; set => Set(ref _isBodyLoading, value); }
    public string DownloadDirectory
    {
        get => _downloadDirectory;
        set => Set(ref _downloadDirectory, value);
    }
    public bool IsSyncing
    {
        get => _isSyncing;
        set { if (Set(ref _isSyncing, value)) { OnPropertyChanged(nameof(CanSync)); OnPropertyChanged(nameof(SyncButtonText)); } }
    }
    public bool CanSync => !IsSyncing;
    public string SyncButtonText => IsSyncing ? "正在同步…" : "同步邮件";
    public bool HasSelectedMessage
    {
        get => _hasSelectedMessage;
        private set { if (Set(ref _hasSelectedMessage, value)) { OnPropertyChanged(nameof(IsListVisible)); OnPropertyChanged(nameof(CanDownloadAttachments)); } }
    }
    public bool IsListVisible => !HasSelectedMessage;
    public string SelectedDateText { get => _selectedDateText; private set => Set(ref _selectedDateText, value); }
    public bool CanGoPrevious => AdjacentMessage(-1) is not null;
    public bool CanGoNext => AdjacentMessage(1) is not null;
    public MessageRow? AdjacentMessage(int offset)
    {
        var row = Messages.FirstOrDefault(item => item.Message.Key == _selectedMessageKey);
        if (row is null) return null;
        var index = Messages.IndexOf(row) + offset;
        return index >= 0 && index < Messages.Count ? Messages[index] : null;
    }
    public bool HasSelectedAccount { get => _hasSelectedAccount; set => Set(ref _hasSelectedAccount, value); }
    public Account? ProblemAccount
    {
        get => _problemAccount;
        set
        {
            if (!Set(ref _problemAccount, value)) return;
            OnPropertyChanged(nameof(HasSyncProblem));
            OnPropertyChanged(nameof(NeedsAuthorization));
            OnPropertyChanged(nameof(ProblemTitle));
            OnPropertyChanged(nameof(ProblemSummary));
            OnPropertyChanged(nameof(ProblemDetails));
        }
    }
    public bool HasSyncProblem => ProblemAccount is not null;
    public bool NeedsAuthorization => ProblemAccount?.Status == ConnectionStatus.NeedsAuthorization;
    public string ProblemTitle => ProblemAccount is null ? "" : $"{ProblemAccount.Address} · {(NeedsAuthorization ? "需要更新授权" : "同步失败")}";
    public string ProblemSummary => ProblemDetails.Split('\n')[0];
    public string ProblemDetails => string.IsNullOrWhiteSpace(ProblemAccount?.LastError) ||
        (!ProblemAccount.LastError.Contains('\n') && ProblemAccount.LastError.EndsWith("Exception", StringComparison.Ordinal))
        ? "旧版本未保存具体失败原因，请立即重试以获取详情。" : ProblemAccount.LastError;
    public string ProblemRetryText { get => _problemRetryText; set => Set(ref _problemRetryText, value); }
    public string EmptyTitle { get => _emptyTitle; set => Set(ref _emptyTitle, value); }
    public string EmptyDescription { get => _emptyDescription; set => Set(ref _emptyDescription, value); }

    public void ShowMessages(IEnumerable<MailMessage> messages)
    {
        var labelMap = Accounts.ToDictionary(account => account.Id, account => account.Address);
        Messages.Clear();
        foreach (var message in messages)
            Messages.Add(new MessageRow(message, labelMap.GetValueOrDefault(message.AccountId, "未知账号")));
        RefreshMailCounts();
    }

    public void RefreshMailCounts()
    {
        OnPropertyChanged(nameof(HasMessages));
        MailCountText = $"共{Messages.Count}封 · {Messages.Count(row => row.IsUnread)}封未读";
        FooterText = $"当前显示 {Messages.Count} 封邮件";
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    public void ShowMessage(MailMessage? message)
    {
        HasSelectedMessage = message is not null;
        _selectedMessageKey = message?.Key;
        SelectedDateText = message is null ? "" : message.ReceivedAt.LocalDateTime.ToString("yyyy年M月d日 HH:mm");
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
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
