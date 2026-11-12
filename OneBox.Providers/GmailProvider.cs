using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;
using OneBox.Core;

namespace OneBox.Providers;

public sealed class GmailProvider(string clientId, string clientSecret, ICredentialVault vault, MailProxy? proxy = null) : IMailProvider
{
    static GmailProvider() => MailEncoding.Initialize();
    private const string Api = "https://gmail.googleapis.com/gmail/v1/users/me";
    private const string Scope = "https://www.googleapis.com/auth/gmail.modify";
    private readonly HttpClient _http = new(new HttpClientHandler { Proxy = proxy ?? new MailProxy() });
    private string? _accessToken;
    private string? _refreshFingerprint;
    private DateTimeOffset _accessTokenExpires;
    public ProviderKind Kind => ProviderKind.Gmail;
    public string? LatestHistoryId { get; private set; }

    public async Task<(string Email, string RefreshToken)> AuthorizeAsync(CancellationToken ct)
    {
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/";
        var authorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + Form(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["response_type"] = "code",
            ["scope"] = Scope,
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        });
        string code;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
            code = await ReceiveAuthorizationCodeAsync(listener, state, port, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("等待 Google 浏览器授权超时，请重试");
        }
        finally { listener.Stop(); }
        using var tokenResponse = await _http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = redirect,
                ["grant_type"] = "authorization_code"
            }), ct);
        await RequireSuccessAsync(tokenResponse, ct);
        using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
        var root = tokenJson.RootElement;
        var refresh = root.GetProperty("refresh_token").GetString()
            ?? throw new InvalidOperationException("Google 未返回离线授权令牌");
        _accessToken = root.GetProperty("access_token").GetString();
        _refreshFingerprint = Fingerprint(refresh);
        _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
        using var profile = await GetAsync($"{Api}/profile", refresh, ct);
        return (profile.RootElement.GetProperty("emailAddress").GetString()!, refresh);
    }

    public async Task<ConnectionCheck> TestConnectionAsync(Account account, string secret, CancellationToken ct)
    {
        try
        {
            using var profile = await GetAsync($"{Api}/profile", secret, ct, account.Id);
            var email = profile.RootElement.GetProperty("emailAddress").GetString();
            if (!string.Equals(account.Address, email, StringComparison.OrdinalIgnoreCase))
                return new ConnectionCheck(false, false, "授权账号与填写地址不一致", "授权账号与填写地址不一致");
            return new ConnectionCheck(true, true);
        }
        catch (UnauthorizedAccessException)
        {
            return new ConnectionCheck(false, false, "需要重新授权", "需要重新授权");
        }
        catch (HttpRequestException)
        {
            return new ConnectionCheck(false, false, "网络或代理连接失败", "网络或代理连接失败");
        }
    }

    public async Task<IReadOnlyList<MailMessage>> GetRecentInboxAsync(Account account, string secret,
        SyncCursor? cursor, DateTimeOffset since, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var incrementalValid = false;
        if (!string.IsNullOrWhiteSpace(cursor?.GmailHistoryId))
        {
            try
            {
                string? page = null;
                do
                {
                    var url = $"{Api}/history?startHistoryId={Uri.EscapeDataString(cursor.GmailHistoryId)}&historyTypes=messageAdded&historyTypes=labelAdded&historyTypes=labelRemoved";
                    if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
                    using var history = await GetAsync(url, secret, ct, account.Id);
                    if (history.RootElement.TryGetProperty("history", out var changes))
                        foreach (var change in changes.EnumerateArray())
                        {
                            if (change.TryGetProperty("messages", out var messages))
                                foreach (var item in messages.EnumerateArray())
                                    if (item.TryGetProperty("id", out var id)) ids.Add(id.GetString()!);
                        }
                    LatestHistoryId = history.RootElement.TryGetProperty("historyId", out var historyId)
                        ? historyId.GetString() : cursor.GmailHistoryId;
                    page = history.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
                } while (page is not null);
                incrementalValid = true;
            }
            catch (GmailCursorExpiredException)
            {
                ids.Clear();
            }
        }
        if (incrementalValid && ids.Count == 0) return [];
        if (!incrementalValid)
        {
            var query = Uri.EscapeDataString($"after:{since:yyyy/MM/dd}");
            string? page = null;
            do
            {
                var url = $"{Api}/messages?labelIds=INBOX&maxResults=500&q={query}";
                if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
                using var list = await GetAsync(url, secret, ct, account.Id);
                if (list.RootElement.TryGetProperty("messages", out var messages))
                    foreach (var item in messages.EnumerateArray()) ids.Add(item.GetProperty("id").GetString()!);
                page = list.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            } while (page is not null);
        }
        using var profile = await GetAsync($"{Api}/profile", secret, ct, account.Id);
        LatestHistoryId = profile.RootElement.GetProperty("historyId").GetString();
        var result = new List<MailMessage>();
        foreach (var id in ids)
        {
            JsonDocument item;
            try { item = await GetAsync($"{Api}/messages/{Uri.EscapeDataString(id)}?format=metadata", secret, ct, account.Id); }
            catch (GmailCursorExpiredException) { continue; }
            using (item)
            {
            var root = item.RootElement;
            var labels = root.GetProperty("labelIds").EnumerateArray().Select(label => label.GetString()).ToHashSet();
            if (!labels.Contains("INBOX")) continue;
            var headers = ReadHeaders(root.GetProperty("payload"));
            var from = headers.GetValueOrDefault("From", "");
            result.Add(new MailMessage(account.Id, id, "INBOX", headers.GetValueOrDefault("Subject", "（无主题）"),
                from, from, headers.GetValueOrDefault("To", ""),
                DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(root.GetProperty("internalDate").GetString()!)),
                root.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "",
                !labels.Contains("UNREAD"), labels.Contains("STARRED"), false));
            }
        }
        return result;
    }

    public async Task<MailMessage> GetMessageAsync(Account account, string secret, string serverId, CancellationToken ct)
    {
        using var item = await GetAsync($"{Api}/messages/{Uri.EscapeDataString(serverId)}?format=raw", secret, ct, account.Id);
        var root = item.RootElement;
        var labelSet = root.GetProperty("labelIds").EnumerateArray().Select(label => label.GetString()).ToHashSet();
        using var rawStream = new MemoryStream(FromBase64Url(root.GetProperty("raw").GetString()!));
        using var mime = await MimeMessage.LoadAsync(rawStream, ct);
        var htmlBody = await InlineImages.ResolveAsync(mime, account.Id, serverId, InlineImages.CacheDirectory, ct);
        var sender = mime.From.Mailboxes.FirstOrDefault();
        return new MailMessage(account.Id, serverId, "INBOX", mime.Subject ?? "（无主题）",
            sender?.Name ?? "", sender?.Address ?? "", string.Join(", ", mime.To.Mailboxes.Select(mailbox => mailbox.Address)),
            DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(root.GetProperty("internalDate").GetString()!)),
            root.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "",
            !labelSet.Contains("UNREAD"), labelSet.Contains("STARRED"), mime.Attachments.Any(), mime.TextBody, htmlBody);
    }

    public async Task SetFlagsAsync(Account account, string secret, string serverId,
        bool? isRead, bool? isStarred, CancellationToken ct)
    {
        var add = new List<string>();
        var remove = new List<string>();
        if (isRead is bool read) (read ? remove : add).Add("UNREAD");
        if (isStarred is bool starred) (starred ? add : remove).Add("STARRED");
        var access = await AccessTokenAsync(secret, ct, account.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/messages/{Uri.EscapeDataString(serverId)}/modify")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { addLabelIds = add, removeLabelIds = remove }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await _http.SendAsync(request, ct);
        await RequireSuccessAsync(response, ct);
    }

    public async Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(Account account, string secret,
        string serverId, CancellationToken ct)
    {
        using var item = await GetAsync($"{Api}/messages/{Uri.EscapeDataString(serverId)}?format=full", secret, ct, account.Id);
        var result = new List<MailAttachment>();
        CollectAttachments(item.RootElement.GetProperty("payload"), account.Id, serverId, result);
        return result;
    }

    public async Task DownloadAttachmentAsync(Account account, string secret, MailAttachment attachment,
        string outputPath, CancellationToken ct)
    {
        if (account.Id != attachment.AccountId) throw new InvalidOperationException("附件账号不匹配");
        using var item = await GetAsync($"{Api}/messages/{Uri.EscapeDataString(attachment.MessageServerId)}?format=full", secret, ct, account.Id);
        var part = FindPart(item.RootElement.GetProperty("payload"), attachment.PartId)
            ?? throw new FileNotFoundException("服务器上找不到该附件");
        var body = part.GetProperty("body");
        string encoded;
        if (body.TryGetProperty("data", out var inline)) encoded = inline.GetString()!;
        else
        {
            var id = body.GetProperty("attachmentId").GetString()!;
            using var remote = await GetAsync($"{Api}/messages/{Uri.EscapeDataString(attachment.MessageServerId)}/attachments/{Uri.EscapeDataString(id)}", secret, ct, account.Id);
            encoded = remote.RootElement.GetProperty("data").GetString()!;
        }
        var bytes = FromBase64Url(encoded);
        await using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        try { await output.WriteAsync(bytes, ct); }
        catch { output.Close(); File.Delete(outputPath); throw; }
    }

    public async Task SendAsync(Account account, string secret, OutgoingMessage message, CancellationToken ct)
    {
        if (!MailRules.IsFromAccount(account, message)) throw new InvalidOperationException("发件账号不匹配");
        if (message.To.Count == 0) throw new ArgumentException("请填写收件人", nameof(message));
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(account.Address));
        foreach (var address in message.To) mime.To.Add(MailboxAddress.Parse(address));
        mime.Subject = message.Subject;
        var builder = new BodyBuilder { TextBody = message.TextBody };
        foreach (var path in message.AttachmentPaths) builder.Attachments.Add(path);
        mime.Body = builder.ToMessageBody();
        using var buffer = new MemoryStream();
        await mime.WriteToAsync(buffer, ct);
        var access = await AccessTokenAsync(secret, ct, account.Id);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/messages/send")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { raw = Base64Url(buffer.ToArray()) }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await _http.SendAsync(request, ct);
        await RequireSuccessAsync(response, ct);
    }

    private async Task<JsonDocument> GetAsync(string url, string refresh, CancellationToken ct, Guid? accountId = null)
    {
        var access = await AccessTokenAsync(refresh, ct, accountId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await _http.SendAsync(request, ct);
        await RequireSuccessAsync(response, ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<string> AccessTokenAsync(string refresh, CancellationToken ct, Guid? accountId)
    {
        if (accountId is Guid existingId) refresh = vault.Read(existingId) ?? refresh;
        var fingerprint = Fingerprint(refresh);
        if (_accessToken is not null && _refreshFingerprint == fingerprint && _accessTokenExpires > DateTimeOffset.UtcNow)
            return _accessToken;
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["refresh_token"] = refresh,
                ["grant_type"] = "refresh_token"
            }), ct);
        await RequireSuccessAsync(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        _accessToken = document.RootElement.GetProperty("access_token").GetString()!;
        _refreshFingerprint = fingerprint;
        _accessTokenExpires = DateTimeOffset.UtcNow.AddSeconds(document.RootElement.GetProperty("expires_in").GetInt32() - 60);
        if (accountId is Guid id && document.RootElement.TryGetProperty("refresh_token", out var rotated))
        {
            var newRefresh = rotated.GetString();
            if (!string.IsNullOrWhiteSpace(newRefresh))
            {
                vault.Save(id, newRefresh);
                _refreshFingerprint = Fingerprint(newRefresh);
            }
        }
        return _accessToken;
    }

    private static void CollectAttachments(JsonElement part, Guid accountId, string messageId,
        List<MailAttachment> result)
    {
        if (part.TryGetProperty("filename", out var filename) && !string.IsNullOrWhiteSpace(filename.GetString())
            && part.TryGetProperty("partId", out var partId))
            result.Add(new MailAttachment(accountId, messageId, partId.GetString()!, filename.GetString()!,
                part.GetProperty("mimeType").GetString() ?? "application/octet-stream",
                part.GetProperty("body").GetProperty("size").GetInt64()));
        if (part.TryGetProperty("parts", out var children))
            foreach (var child in children.EnumerateArray()) CollectAttachments(child, accountId, messageId, result);
    }

    private static JsonElement? FindPart(JsonElement part, string partId)
    {
        if (part.TryGetProperty("partId", out var current) && current.GetString() == partId) return part;
        if (part.TryGetProperty("parts", out var children))
            foreach (var child in children.EnumerateArray())
                if (FindPart(child, partId) is JsonElement found) return found;
        return null;
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in payload.GetProperty("headers").EnumerateArray())
        {
            var name = header.GetProperty("name").GetString();
            if (name is not null && !result.ContainsKey(name))
                result[name] = header.GetProperty("value").GetString() ?? "";
        }
        return result;
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.NotFound) throw new GmailCursorExpiredException();
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Gmail 授权已失效");
        var details = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.BadRequest &&
            details.Contains("\"invalid_grant\"", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Gmail 授权已过期或被撤销，请重新授权");
        throw new HttpRequestException($"Gmail 请求失败：{(int)response.StatusCode} {details[..Math.Min(160, details.Length)]}");
    }

    private static string Form(Dictionary<string, string> values) =>
        string.Join("&", values.Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
    private static async Task<string> ReceiveAuthorizationCodeAsync(
        TcpListener listener, string state, int port, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync().WaitAsync(ct);
            var parts = requestLine?.Split(' ', 3);
            if (parts is not { Length: 3 } || parts[0] != "GET" ||
                !Uri.TryCreate($"http://127.0.0.1:{port}{parts[1]}", UriKind.Absolute, out var callback) ||
                callback.Host != "127.0.0.1" || callback.Port != port)
            {
                await WriteBrowserResponseAsync(stream, "无效的授权回调。", ct);
                continue;
            }
            var receivedState = QueryValue(callback, "state") ?? "";
            var valid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(receivedState), Encoding.UTF8.GetBytes(state));
            if (!valid)
            {
                await WriteBrowserResponseAsync(stream, "授权状态不匹配，请返回 OneBox 重试。", ct);
                continue;
            }
            var error = QueryValue(callback, "error");
            var code = QueryValue(callback, "code");
            await WriteBrowserResponseAsync(stream,
                code is not null && error is null
                    ? "已收到 Google 授权码。请返回 OneBox 查看最终连接结果。"
                    : "授权已取消，请返回 OneBox。", ct);
            if (error is not null || code is null) throw new InvalidOperationException("Google 授权已取消");
            return code;
        }
    }

    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var item in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = item.Split('=', 2);
            if (WebUtility.UrlDecode(pair[0]) == name)
                return pair.Length == 2 ? WebUtility.UrlDecode(pair[1]) : "";
        }
        return null;
    }

    private static async Task WriteBrowserResponseAsync(NetworkStream stream, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private sealed class GmailCursorExpiredException : Exception { }
}
