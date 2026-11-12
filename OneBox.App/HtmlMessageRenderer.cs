using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using OneBox.Providers;

namespace OneBox.App;

public sealed class HtmlMessageRenderer(CoreWebView2 web)
{
    private static readonly string RemoteImagesDocumentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(CreateDocument(true)));
    private static readonly string LocalImagesDocumentUri = "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(CreateDocument(false)));
    private Task? _documentReady;
    private bool _allowRemoteImages;
    private long _revision;

    public async Task<bool> ShowAsync(string htmlBody, bool allowRemoteImages, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revision = ++_revision;
        if (_documentReady is null || _documentReady.IsFaulted || _allowRemoteImages != allowRemoteImages)
        {
            _allowRemoteImages = allowRemoteImages;
            _documentReady = LoadDocumentAsync(allowRemoteImages);
        }
        await _documentReady.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (revision != _revision) return false;
        await web.ExecuteScriptAsync("""
            (() => {
                const content = new DOMParser().parseFromString(
            """ + JsonSerializer.Serialize(htmlBody) + """
                , 'text/html');
                content.querySelectorAll('meta[http-equiv="refresh" i]').forEach(item => item.remove());
                const fragment = document.createDocumentFragment();
                content.head.querySelectorAll('style').forEach(item => fragment.append(item));
                fragment.append(...content.body.childNodes);
                for (const [target, original] of [[document.documentElement, content.documentElement], [document.body, content.body]]) {
                    for (const attribute of [...target.attributes]) target.removeAttribute(attribute.name);
                    for (const attribute of original.attributes) target.setAttribute(attribute.name, attribute.value);
                }
                document.body.replaceChildren(fragment);
                window.scrollTo(0, 0);
            })();
            """);
        cancellationToken.ThrowIfCancellationRequested();
        return revision == _revision;
    }

    private async Task LoadDocumentAsync(bool allowRemoteImages)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong navigationId = 0;
        EventHandler<CoreWebView2NavigationStartingEventArgs> starting = (_, e) => navigationId = e.NavigationId;
        EventHandler<CoreWebView2DOMContentLoadedEventArgs> loaded = (_, e) =>
        {
            if (e.NavigationId == navigationId) ready.TrySetResult();
        };
        EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = (_, e) =>
        {
            if (e.NavigationId == navigationId && !e.IsSuccess)
                ready.TrySetException(new InvalidOperationException($"HTML阅读页面加载失败：{e.WebErrorStatus}"));
        };
        web.NavigationStarting += starting;
        web.DOMContentLoaded += loaded;
        web.NavigationCompleted += completed;
        try
        {
            web.NavigateToString(CreateDocument(allowRemoteImages));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            web.NavigationStarting -= starting;
            web.DOMContentLoaded -= loaded;
            web.NavigationCompleted -= completed;
        }
    }

    public async Task<string> ReadSelectedTextAsync() =>
        JsonSerializer.Deserialize<string>(await web.ExecuteScriptAsync("window.getSelection()?.toString() ?? ''")) ?? "";

    public static bool IsReaderDocument(string address) => address == RemoteImagesDocumentUri || address == LocalImagesDocumentUri ||
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "about" && uri.AbsolutePath == "blank";

    private static string CreateDocument(bool allowRemoteImages)
    {
        var imagePolicy = allowRemoteImages ? "img-src data: https: http:;" : $"img-src data: https://{InlineImages.Host};";
        return """
                <!doctype html><html><head><meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none';
                """ + imagePolicy + """
                 style-src 'unsafe-inline'; font-src data:">
                <style>html,body{margin:0;background:#fff;color:#193447;font:14px/1.7 'Segoe UI Variable Text','Microsoft YaHei UI',sans-serif}body{overflow-wrap:anywhere}img{max-width:100%;height:auto}table{max-width:100%}</style>
                </head><body></body></html>
                """;
    }
}
