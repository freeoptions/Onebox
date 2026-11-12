using Microsoft.Web.WebView2.Core;

namespace OneBox.App;

public sealed class MailLinkNavigation(Action<Uri> openExternal)
{
    public void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (HtmlMessageRenderer.IsReaderDocument(e.Uri)) return;
        e.Cancel = true;
        OpenExternalLink(e.Uri, e.IsUserInitiated);
    }

    public void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalLink(e.Uri, e.IsUserInitiated);
    }

    private void OpenExternalLink(string address, bool isUserInitiated)
    {
        if (isUserInitiated && Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host))
            openExternal(uri);
    }
}
