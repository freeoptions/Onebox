namespace OneBox.Core;

public enum ProviderKind { Gmail, Qq, NetEase }
public enum ConnectionStatus { Offline, Connecting, Online, NeedsAuthorization, Failed }
public enum FolderKind { Inbox, Sent, Drafts, Archive, Trash, Other }
public enum PendingActionKind { MarkRead, MarkUnread, Star, Unstar }

public sealed record Account(
    Guid Id, string Address, string DisplayName, ProviderKind Provider,
    ConnectionStatus Status = ConnectionStatus.Offline,
    string? LastError = null);

public sealed record MailFolder(
    Guid AccountId, string ServerId, string Name, FolderKind Kind,
    long? UidValidity = null, long? HighestUid = null);

public sealed record MailMessage(
    Guid AccountId, string ServerId, string FolderId, string Subject,
    string FromName, string FromAddress, string ToAddresses, DateTimeOffset ReceivedAt,
    string Preview, bool IsRead, bool IsStarred, bool HasAttachments,
    string? TextBody = null, string? HtmlBody = null)
{
    public string Key => $"{AccountId:N}:{ServerId}";
}

public sealed record MailAttachment(
    Guid AccountId, string MessageServerId, string PartId,
    string FileName, string ContentType, long Size);

public sealed record SyncCursor(
    Guid AccountId, string FolderId, string? GmailHistoryId,
    long? UidValidity, long? HighestUid, DateTimeOffset? LastSuccess);

public sealed record PendingAction(
    Guid Id, Guid AccountId, string MessageServerId, PendingActionKind Kind,
    DateTimeOffset CreatedAt, int Attempts = 0);

public sealed record OutgoingMessage(
    Guid AccountId, IReadOnlyList<string> To, string Subject,
    string TextBody, IReadOnlyList<string> AttachmentPaths);

public sealed record ConnectionCheck(bool ReceivingSucceeded, bool SendingSucceeded,
    string? ReceivingError = null, string? SendingError = null);
