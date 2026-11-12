namespace OneBox.Core;

public enum ProxyMode { System, Direct, Custom }

public sealed record ProxySettings
{
    public ProxyMode Mode { get; init; } = ProxyMode.System;
    public string Address { get; init; } = "";

    public Uri ParseAddress()
    {
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "socks5") || string.IsNullOrEmpty(uri.Host) ||
            uri.Port is < 1 or > 65535 || uri.UserInfo.Length > 0 ||
            uri.AbsolutePath != "/" && uri.AbsolutePath.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("请填写有效的HTTP、HTTPS或SOCKS5代理地址和端口，不包含账号密码或路径。");
        return uri;
    }
}
