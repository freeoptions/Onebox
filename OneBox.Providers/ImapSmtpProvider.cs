using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using OneBox.Core;

namespace OneBox.Providers;

public sealed class ImapSmtpProvider(ProviderKind kind, MailProxy? proxy = null) : IMailProvider
{
    static ImapSmtpProvider() => MailEncoding.Initialize();
    private readonly MailProxy _proxy = proxy ?? new MailProxy();
    public ProviderKind Kind { get; } = kind is ProviderKind.Qq or ProviderKind.NetEase or ProviderKind.GmailImap
        ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
    public long? CurrentUidValidity { get; private set; }
    public long? CurrentHighestUid { get; private set; }
    public SyncCursor? CurrentJunkCursor { get; private set; }
    public IReadOnlySet<string> CurrentMessageIdSet { get; private set; } = new HashSet<string>();

    public static (string Imap, int ImapPort, string Smtp, int SmtpPort) Servers(string address)
    {
        var domain = address.Split('@').LastOrDefault()?.ToLowerInvariant();
        return domain switch
        {
            "gmail.com" or "googlemail.com" => ("imap.gmail.com", 993, "smtp.gmail.com", 465),
            "qq.com" => ("imap.qq.com", 993, "smtp.qq.com", 465),
            "163.com" => ("imap.163.com", 993, "smtp.163.com", 465),
            "126.com" => ("imap.126.com", 993, "smtp.126.com", 465),
            "yeah.net" => ("imap.yeah.net", 993, "smtp.yeah.net", 465),
            _ => throw new ArgumentException("暂不支持该邮箱域名", nameof(address))
        };
    }

    internal async Task<ImapClient> OpenImapAsync(Account account, string secret, CancellationToken ct, Action<string>? onStage = null)
    {
        var endpoint = Servers(account.Address);
        var client = new ImapClient();
        try
        {
            client.ProxyClient = _proxy.CreateClient(endpoint.Imap, endpoint.ImapPort);
            onStage?.Invoke($"连接IMAP服务器及TLS握手（{endpoint.Imap}:{endpoint.ImapPort}，代理：{(client.ProxyClient is null ? "直连" : $"{client.ProxyClient.ProxyHost}:{client.ProxyClient.ProxyPort}")}）");
            await client.ConnectAsync(endpoint.Imap, endpoint.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            onStage?.Invoke("IMAP登录认证");
            await client.AuthenticateAsync(account.Address, secret, ct);
            if (Kind == ProviderKind.NetEase && client.Capabilities.HasFlag(ImapCapabilities.Id))
            {
                await client.IdentifyAsync(new ImapImplementation
                {
                    Name = "OneBox",
                    Version = typeof(ImapSmtpProvider).Assembly.GetName().Version?.ToString(),
                    Vendor = "OneBox",
                    SupportUrl = "https://github.com/freeoptions/Onebox"
                }, ct);
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<SmtpClient> OpenSmtpAsync(Account account, string secret, CancellationToken ct)
    {
        var endpoint = Servers(account.Address);
        var client = new SmtpClient();
        try
        {
            client.ProxyClient = _proxy.CreateClient(endpoint.Smtp, endpoint.SmtpPort);
            await client.ConnectAsync(endpoint.Smtp, endpoint.SmtpPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(account.Address, secret, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<ConnectionCheck> TestConnectionAsync(Account account, string secret, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? receivingError = null;
        string? sendingError = null;
        using var receivingTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        receivingTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var imap = await OpenImapAsync(account, secret, receivingTimeout.Token);
            await imap.DisconnectAsync(true, receivingTimeout.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            receivingError = Describe(error);
        }
        using var sendingTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendingTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var smtp = await OpenSmtpAsync(account, secret, sendingTimeout.Token);
            await smtp.DisconnectAsync(true, sendingTimeout.Token);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            sendingError = Describe(error);
        }
        return new ConnectionCheck(receivingError is null, sendingError is null, receivingError, sendingError);
    }

    internal static async Task<IMailFolder?> FindJunkFolderAsync(ImapClient client, CancellationToken ct)
    {
        var junk = (client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0
            ? client.GetFolder(SpecialFolder.Junk) : null;
        if (junk is not null) return junk;
        foreach (var folderNamespace in client.PersonalNamespaces)
        {
            var folderList = await client.GetFoldersAsync(folderNamespace, StatusItems.None, false, ct);
            junk = folderList.FirstOrDefault(folder => folder.Attributes.HasFlag(FolderAttributes.Junk));
            junk ??= folderList.FirstOrDefault(folder => !folder.Attributes.HasFlag(FolderAttributes.Trash) &&
                (folder.Name.Equals("Junk", StringComparison.OrdinalIgnoreCase) ||
                 folder.Name.Equals("Spam", StringComparison.OrdinalIgnoreCase) || folder.Name is "垃圾邮件" or "垃圾箱"));
            if (junk is not null) return junk;
        }
        return null;
    }

    public async Task<IReadOnlyList<MailMessage>> GetRecentInboxAsync(Account account, string secret,
        SyncCursor? cursor, DateTimeOffset since, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var folderList = new List<IMailFolder> { client.Inbox };
        var junk = await FindJunkFolderAsync(client, ct);
        if (junk is not null && junk.FullName != client.Inbox.FullName) folderList.Add(junk);
        var messageList = new List<MailMessage>();
        var messageIdSet = new HashSet<string>();
        CurrentJunkCursor = null;
        foreach (var folder in folderList)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            var isInbox = folder == client.Inbox;
            if (isInbox)
            {
                CurrentUidValidity = folder.UidValidity;
                CurrentHighestUid = folder.UidNext is UniqueId next ? next.Id - 1L : null;
            }
            var uidList = await folder.SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime.Date), ct);
            var orderedUidList = uidList.OrderByDescending(uid => uid.Id).ToList();
            string ServerId(UniqueId uid) => new ImapMessageLocation(isInbox ? null : folder.FullName,
                isInbox ? null : folder.UidValidity, uid).ServerId;
            foreach (var uid in uidList) messageIdSet.Add(ServerId(uid));
            if (!isInbox)
                CurrentJunkCursor = new SyncCursor(account.Id, "JUNK", null, folder.UidValidity,
                    folder.UidNext is UniqueId junkNext ? junkNext.Id - 1L :
                        orderedUidList.Select(uid => (long)uid.Id).DefaultIfEmpty(0).Max(), DateTimeOffset.UtcNow);
            var fullScan = !isInbox || cursor is null || cursor.UidValidity != CurrentUidValidity;
            var recentUidList = fullScan ? orderedUidList : orderedUidList.Take(300)
                .Concat(orderedUidList.Where(uid => uid.Id > (cursor!.HighestUid ?? 0)))
                .Distinct().ToList();
            foreach (var uidBatchList in CreateFetchBatches(recentUidList))
            {
                var summaryList = await folder.FetchAsync(uidBatchList, MessageSummaryItems.Envelope |
                    MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure, ct);
                messageList.AddRange(summaryList.Select(summary => new MailMessage(
                    account.Id, ServerId(summary.UniqueId), "INBOX",
                    summary.Envelope?.Subject ?? "（无主题）", summary.Envelope?.From.Mailboxes.FirstOrDefault()?.Name ?? "",
                    summary.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                    string.Join(", ", summary.Envelope?.To.Mailboxes.Select(mailbox => mailbox.Address) ?? []),
                    summary.InternalDate ?? DateTimeOffset.MinValue,
                    "", summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                    summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
                    summary.Attachments?.Any() ?? false)));
            }
        }
        CurrentMessageIdSet = messageIdSet;
        await client.DisconnectAsync(true, ct);
        return messageList;
    }

    private static async Task<(IMailFolder Folder, UniqueId Uid)> OpenMessageFolderAsync(
        ImapClient client, string serverId, FolderAccess access, CancellationToken ct)
    {
        var location = ImapMessageLocation.Parse(serverId);
        var folder = location.FolderName is null ? client.Inbox : await client.GetFolderAsync(location.FolderName, ct);
        await folder.OpenAsync(access, ct);
        if (location.UidValidity is uint validity && folder.UidValidity != validity)
            throw new MessageNotFoundException("邮件所在文件夹已重建，请重新同步后打开邮件。");
        return (folder, location.Uid);
    }

    internal static IEnumerable<IList<UniqueId>> CreateFetchBatches(IEnumerable<UniqueId> uidList)
    {
        // 使用正序范围和短批次，避免服务端拒绝倒序范围或截断较长的UID参数。
        foreach (var uidBatch in uidList.Distinct().OrderBy(uid => uid.Id).Chunk(50))
            yield return new UniqueIdSet(uidBatch, SortOrder.Ascending);
    }

    public async Task<MailMessage> GetMessageAsync(Account account, string secret, string serverId, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var (inbox, uid) = await OpenMessageFolderAsync(client, serverId, FolderAccess.ReadOnly, ct);
        using var mime = await inbox.GetMessageAsync(uid, ct);
        var htmlBody = await InlineImages.ResolveAsync(mime, account.Id, serverId, InlineImages.CacheDirectory, ct);
        var result = new MailMessage(account.Id, serverId, "INBOX", mime.Subject ?? "（无主题）",
            mime.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            mime.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            string.Join(", ", mime.To.Mailboxes.Select(mailbox => mailbox.Address)),
            mime.Date, mime.TextBody?.Replace('\n', ' ').Trim() ?? "",
            false, false, mime.Attachments.Any(), mime.TextBody, htmlBody);
        await client.DisconnectAsync(true, ct);
        return result;
    }

    public async Task SetFlagsAsync(Account account, string secret, string serverId,
        bool? isRead, bool? isStarred, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var (inbox, uid) = await OpenMessageFolderAsync(client, serverId, FolderAccess.ReadWrite, ct);
        var uidList = new[] { uid };
        if (isRead is bool read)
        {
            if (read) await inbox.AddFlagsAsync(uidList, MessageFlags.Seen, true, ct);
            else await inbox.RemoveFlagsAsync(uidList, MessageFlags.Seen, true, ct);
        }
        if (isStarred is bool starred)
        {
            if (starred) await inbox.AddFlagsAsync(uidList, MessageFlags.Flagged, true, ct);
            else await inbox.RemoveFlagsAsync(uidList, MessageFlags.Flagged, true, ct);
        }
        await client.DisconnectAsync(true, ct);
    }

    public async Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(Account account, string secret,
        string serverId, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var (folder, uid) = await OpenMessageFolderAsync(client, serverId, FolderAccess.ReadOnly, ct);
        using var mime = await folder.GetMessageAsync(uid, ct);
        var attachmentList = mime.Attachments.Select((part, index) =>
            new MailAttachment(account.Id, serverId, index.ToString(),
                part is MimePart filePart ? filePart.FileName ?? "附件" : part.ContentDisposition?.FileName ?? "邮件附件.eml",
                part.ContentType.MimeType, part is MimePart sizedPart ? sizedPart.Content?.Stream?.Length ?? 0 : 0)).ToList();
        await client.DisconnectAsync(true, ct);
        return attachmentList;
    }

    public async Task DownloadAttachmentAsync(Account account, string secret, MailAttachment attachment,
        string outputPath, CancellationToken ct)
    {
        if (account.Id != attachment.AccountId) throw new InvalidOperationException("附件账号不匹配");
        using var client = await OpenImapAsync(account, secret, ct);
        var (folder, uid) = await OpenMessageFolderAsync(client, attachment.MessageServerId, FolderAccess.ReadOnly, ct);
        using var mime = await folder.GetMessageAsync(uid, ct);
        var part = mime.Attachments.ElementAt(int.Parse(attachment.PartId));
        await MimeAttachments.SaveAsync(part, outputPath, ct);
        await client.DisconnectAsync(true, ct);
    }

    public async Task SendAsync(Account account, string secret, OutgoingMessage message, CancellationToken ct)
    {
        if (!MailRules.IsFromAccount(account, message)) throw new InvalidOperationException("发件账号不匹配");
        if (message.To.Count == 0) throw new ArgumentException("请填写收件人", nameof(message));
        var builder = new BodyBuilder { TextBody = message.TextBody };
        foreach (var path in message.AttachmentPaths) builder.Attachments.Add(path);
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(account.DisplayName, account.Address));
        foreach (var address in message.To) mime.To.Add(MailboxAddress.Parse(address));
        mime.Subject = message.Subject;
        mime.Body = builder.ToMessageBody();
        using var client = await OpenSmtpAsync(account, secret, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, ct);
    }

    private string Describe(Exception error) => error switch
    {
        OperationCanceledException or TimeoutException => "连接超时，请检查设置中的代理及网络后重试",
        NotSupportedException or ArgumentException => "代理配置无效，请检查设置中的代理协议、地址和端口",
        MailKit.Net.Proxy.ProxyProtocolException => "代理连接失败，请检查代理是否运行、地址及端口是否正确",
        SslHandshakeException => "安全连接验证失败，请检查系统时间、证书及网络是否允许访问证书验证服务",
        MailKit.Security.AuthenticationException => Kind == ProviderKind.GmailImap
            ? "应用专用密码无效，请确认已开启两步验证并生成应用专用密码；不能使用Google登录密码"
            : "授权码错误或客户端协议尚未开启",
        System.Security.Authentication.AuthenticationException => "证书验证失败",
        System.Net.Sockets.SocketException => "网络连接失败，请检查网络或代理",
        _ => "连接失败，请检查邮箱设置与网络"
    };
}
