using System.Net.Sockets;
using System.Text.RegularExpressions;
using OneBox.Core;

namespace OneBox.Providers;

public sealed record SyncFailure(string Summary, string Advice, string Details, bool NeedsAuthorization)
{
    public string DisplayText => $"{Summary}\n{Advice}\n\n{Details}";

    internal static bool IsUnexpectedDisconnect(Exception error) =>
        error is MailKit.Net.Imap.ImapProtocolException &&
        error.Message.Equals("The IMAP server has unexpectedly disconnected.", StringComparison.Ordinal);

    public static SyncFailure Describe(Exception error, ProviderKind provider, string stage,
        params string?[] sensitiveValues)
    {
        var exceptionList = new List<Exception>();
        for (Exception? current = error; current is not null; current = current.InnerException)
            exceptionList.Add(current);
        var authentication = exceptionList.Any(item => item is MailKit.Security.AuthenticationException or UnauthorizedAccessException);
        var timeout = exceptionList.Any(item => item is TimeoutException or OperationCanceledException);
        var disconnected = exceptionList.Any(IsUnexpectedDisconnect);
        var network = exceptionList.Any(item => item is SocketException or HttpRequestException or IOException);
        var tlsHandshake = exceptionList.Any(item => item is MailKit.Security.SslHandshakeException);
        var certificate = exceptionList.OfType<MailKit.Security.SslHandshakeException>().Any(item => item.ServerCertificate is not null) ||
            (!network && exceptionList.Any(item => item is System.Security.Authentication.AuthenticationException));
        var summary = authentication ? "邮箱拒绝了登录授权" : timeout ? "同步请求超时" :
            certificate ? "邮箱服务器证书验证失败" : tlsHandshake ? (network ? "TLS连接中断" : "TLS握手失败") :
            disconnected ? "邮箱服务器连接中断" : network ? "连接邮箱服务器失败" : "邮箱同步未完成";
        var advice = authentication
            ? provider switch
            {
                ProviderKind.Gmail => "请重新授权Google账号后再同步。",
                ProviderKind.GmailImap => "请确认Google账号已开启两步验证，并更新应用专用密码；这里需要的不是Google登录密码。",
                _ => "请确认已开启IMAP/SMTP，并更新客户端授权码；这里需要的不是邮箱登录密码。"
            }
            : certificate ? "请检查系统时间、证书信任链及证书吊销检查服务是否可访问后重试。"
            : tlsHandshake ? "请检查代理节点是否可用及网络是否稳定，恢复连接后重试；仍失败时查看下方TLS握手原因。"
            : disconnected ? "邮箱连接已断开，需要重新连接；仅凭此异常无法确定断开原因。"
            : timeout || network ? "请检查网络或代理后重试；若网络正常，可稍后再次同步。"
            : "请查看下方服务器返回的原因，再重试同步。";
        var details = $"失败阶段：{stage}\n" + string.Join("\n", exceptionList.Select(item => $"{item.GetType().Name}: {item.Message}"));
        foreach (var sensitiveValue in sensitiveValues.Where(value => !string.IsNullOrEmpty(value)).OrderByDescending(value => value!.Length))
        {
            details = details.Replace(sensitiveValue!, "[已隐藏]", StringComparison.Ordinal);
            details = details.Replace(Uri.EscapeDataString(sensitiveValue!), "[已隐藏]", StringComparison.Ordinal);
        }
        details = Regex.Replace(details,
            @"(?i)(Bearer\s+)[^\s""<>]+|((?:access_token|refresh_token|client_secret|password|authorization)\s*[""']?\s*[:=]\s*[""']?(?:Bearer\s+)?)[^\s""'&,}]+",
            match => (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) + "[已隐藏]");
        return new SyncFailure(summary, advice, details.Length > 6000 ? details[..6000] : details, authentication);
    }
}
