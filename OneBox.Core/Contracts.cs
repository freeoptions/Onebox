namespace OneBox.Core;

public interface ICredentialVault
{
    void Save(Guid accountId, string secret);
    string? Read(Guid accountId);
    void Delete(Guid accountId);
}

public interface IMailStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task UpsertAccountAsync(Account account, CancellationToken cancellationToken = default);
    Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessage>> GetMessagesAsync(Guid? accountId, string? folderId,
        string? query, int limit = 200, bool starredOnly = false, CancellationToken cancellationToken = default);
    Task UpsertMessagesAsync(IEnumerable<MailMessage> messages, CancellationToken cancellationToken = default);
    Task ResetFolderAsync(Guid accountId, string folderId, CancellationToken cancellationToken = default);
    Task<SyncCursor?> GetCursorAsync(Guid accountId, string folderId, CancellationToken cancellationToken = default);
    Task SaveCursorAsync(SyncCursor cursor, CancellationToken cancellationToken = default);
    Task EnqueueAsync(PendingAction action, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingAction>> GetPendingAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task CompletePendingAsync(Guid actionId, CancellationToken cancellationToken = default);
}

public interface IMailProvider
{
    ProviderKind Kind { get; }
    Task<ConnectionCheck> TestConnectionAsync(Account account, string secret, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailMessage>> GetRecentInboxAsync(Account account, string secret,
        SyncCursor? cursor, DateTimeOffset since, CancellationToken cancellationToken);
    Task<MailMessage> GetMessageAsync(Account account, string secret, string serverId,
        CancellationToken cancellationToken);
    Task SetFlagsAsync(Account account, string secret, string serverId,
        bool? isRead, bool? isStarred, CancellationToken cancellationToken);
    Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(Account account, string secret,
        string serverId, CancellationToken cancellationToken);
    Task DownloadAttachmentAsync(Account account, string secret, MailAttachment attachment,
        string outputPath, CancellationToken cancellationToken);
    Task SendAsync(Account account, string secret, OutgoingMessage message,
        CancellationToken cancellationToken);
}
