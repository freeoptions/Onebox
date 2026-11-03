using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using OneBox.Core;

namespace OneBox.Providers;

public sealed class ImapSmtpProvider(ProviderKind kind) : IMailProvider
{
    public ProviderKind Kind { get; } = kind is ProviderKind.Qq or ProviderKind.NetEase
        ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
    public long? CurrentUidValidity { get; private set; }

    public static (string Imap, int ImapPort, string Smtp, int SmtpPort) Servers(string address)
    {
        var domain = address.Split('@').LastOrDefault()?.ToLowerInvariant();
        return domain switch
        {
            "qq.com" => ("imap.qq.com", 993, "smtp.qq.com", 465),
            "163.com" => ("imap.163.com", 993, "smtp.163.com", 465),
            "126.com" => ("imap.126.com", 993, "smtp.126.com", 465),
            "yeah.net" => ("imap.yeah.net", 993, "smtp.yeah.net", 465),
            _ => throw new ArgumentException("暂不支持该邮箱域名", nameof(address))
        };
    }

    private async Task<ImapClient> OpenImapAsync(Account account, string secret, CancellationToken ct)
    {
        var endpoint = Servers(account.Address);
        var client = new ImapClient();
        try
        {
            await client.ConnectAsync(endpoint.Imap, endpoint.ImapPort, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(account.Address, secret, ct);
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
        string? receivingError = null;
        string? sendingError = null;
        try
        {
            using var imap = await OpenImapAsync(account, secret, ct);
            await imap.DisconnectAsync(true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            receivingError = Describe(error);
        }
        try
        {
            using var smtp = await OpenSmtpAsync(account, secret, ct);
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            sendingError = Describe(error);
        }
        return new ConnectionCheck(receivingError is null, sendingError is null, receivingError, sendingError);
    }

    public async Task<IReadOnlyList<MailMessage>> GetRecentInboxAsync(Account account, string secret,
        SyncCursor? cursor, DateTimeOffset since, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        CurrentUidValidity = inbox.UidValidity;
        var uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime.Date), ct);
        var ordered = uids.OrderByDescending(uid => uid.Id).ToList();
        var fullScan = cursor is null || cursor.UidValidity != CurrentUidValidity;
        var recent = fullScan ? ordered : ordered.Take(300)
            .Concat(ordered.Where(uid => uid.Id > (cursor!.HighestUid ?? 0)))
            .Distinct().ToList();
        if (recent.Count == 0) return [];
        var result = new List<MailMessage>(recent.Count);
        foreach (var batch in recent.Chunk(250))
        {
            var summaries = await inbox.FetchAsync(batch, MessageSummaryItems.Envelope |
                MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.BodyStructure, ct);
            result.AddRange(summaries.Select(summary => new MailMessage(
                account.Id, summary.UniqueId.Id.ToString(), "INBOX",
                summary.Envelope?.Subject ?? "（无主题）",
                summary.Envelope?.From.Mailboxes.FirstOrDefault()?.Name ?? "",
                summary.Envelope?.From.Mailboxes.FirstOrDefault()?.Address ?? "",
                string.Join(", ", summary.Envelope?.To.Mailboxes.Select(mailbox => mailbox.Address) ?? []),
                summary.InternalDate ?? DateTimeOffset.MinValue,
                "", summary.Flags?.HasFlag(MessageFlags.Seen) ?? false,
                summary.Flags?.HasFlag(MessageFlags.Flagged) ?? false,
                summary.Attachments?.Any() ?? false)));
        }
        await client.DisconnectAsync(true, ct);
        return result;
    }

    public async Task<MailMessage> GetMessageAsync(Account account, string secret, string serverId, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var uid = new UniqueId(uint.Parse(serverId));
        var mime = await inbox.GetMessageAsync(uid, ct);
        var result = new MailMessage(account.Id, serverId, "INBOX", mime.Subject ?? "（无主题）",
            mime.From.Mailboxes.FirstOrDefault()?.Name ?? "",
            mime.From.Mailboxes.FirstOrDefault()?.Address ?? "",
            string.Join(", ", mime.To.Mailboxes.Select(mailbox => mailbox.Address)),
            mime.Date, mime.TextBody?.Replace('\n', ' ').Trim() ?? "",
            false, false, mime.Attachments.Any(), mime.TextBody, mime.HtmlBody);
        await client.DisconnectAsync(true, ct);
        return result;
    }

    public async Task SetFlagsAsync(Account account, string secret, string serverId,
        bool? isRead, bool? isStarred, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        var uids = new[] { new UniqueId(uint.Parse(serverId)) };
        if (isRead is bool read)
        {
            if (read) await inbox.AddFlagsAsync(uids, MessageFlags.Seen, true, ct);
            else await inbox.RemoveFlagsAsync(uids, MessageFlags.Seen, true, ct);
        }
        if (isStarred is bool starred)
        {
            if (starred) await inbox.AddFlagsAsync(uids, MessageFlags.Flagged, true, ct);
            else await inbox.RemoveFlagsAsync(uids, MessageFlags.Flagged, true, ct);
        }
        await client.DisconnectAsync(true, ct);
    }

    public async Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(Account account, string secret,
        string serverId, CancellationToken ct)
    {
        using var client = await OpenImapAsync(account, secret, ct);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var mime = await client.Inbox.GetMessageAsync(new UniqueId(uint.Parse(serverId)), ct);
        var result = mime.Attachments.OfType<MimePart>().Select((part, index) =>
            new MailAttachment(account.Id, serverId, index.ToString(), part.FileName ?? "附件",
                part.ContentType.MimeType, part.Content?.Stream?.Length ?? 0)).ToArray();
        await client.DisconnectAsync(true, ct);
        return result;
    }

    public async Task DownloadAttachmentAsync(Account account, string secret, MailAttachment attachment,
        string outputPath, CancellationToken ct)
    {
        if (account.Id != attachment.AccountId) throw new InvalidOperationException("附件账号不匹配");
        using var client = await OpenImapAsync(account, secret, ct);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var mime = await client.Inbox.GetMessageAsync(new UniqueId(uint.Parse(attachment.MessageServerId)), ct);
        var part = mime.Attachments.OfType<MimePart>().ElementAt(int.Parse(attachment.PartId));
        if (part.Content is null) throw new InvalidDataException("附件内容为空");
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { await part.Content.DecodeToAsync(output, ct); }
        catch { output.Close(); File.Delete(outputPath); throw; }
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

    private static string Describe(Exception error) => error switch
    {
        MailKit.Security.AuthenticationException => "授权码错误或客户端协议尚未开启",
        System.Security.Authentication.AuthenticationException => "证书验证失败",
        System.Net.Sockets.SocketException => "网络连接失败，请检查网络或代理",
        _ => "连接失败，请检查邮箱设置与网络"
    };
}
