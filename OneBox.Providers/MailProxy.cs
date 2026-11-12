using System.Net;
using MailKit.Net.Proxy;
using OneBox.Core;

namespace OneBox.Providers;

public sealed class MailProxy : IWebProxy
{
    public ProxySettings Settings { get; set; } = new();
    public ICredentials? Credentials { get; set; }

    public Uri? GetProxy(Uri destination) => Settings.Mode switch
    {
        ProxyMode.System => HttpClient.DefaultProxy.IsBypassed(destination)
            ? null : HttpClient.DefaultProxy.GetProxy(destination),
        ProxyMode.Direct => null,
        ProxyMode.Custom => Settings.ParseAddress(),
        _ => throw new InvalidOperationException("代理方式无效，请重新选择网络设置。")
    };

    public bool IsBypassed(Uri host) => GetProxy(host) is not Uri proxy || proxy == host;

    internal IProxyClient? CreateClient(string host, int port)
    {
        var destination = new UriBuilder("https", host, port).Uri;
        var proxy = GetProxy(destination);
        if (proxy is null || proxy == destination) return null;
        return proxy.Scheme switch
        {
            "http" => new HttpProxyClient(proxy.Host, proxy.Port),
            "https" => new HttpsProxyClient(proxy.Host, proxy.Port),
            "socks5" => new Socks5Client(proxy.Host, proxy.Port),
            _ => throw new NotSupportedException("系统代理协议暂不支持，请在设置中选择HTTP、HTTPS或SOCKS5代理。")
        };
    }
}
