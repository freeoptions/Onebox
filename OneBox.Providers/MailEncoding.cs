using System.Text;
using System.Text.RegularExpressions;

namespace OneBox.Providers;

public static class MailEncoding
{
    internal const string CacheMarker = "<!--onebox-encoding-v2-->";

    internal static void Initialize() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool NeedsReload(string? html) => html is not null && !html.Contains(CacheMarker, StringComparison.Ordinal)
        && Regex.IsMatch(html, @"charset\s*=\s*[""']?\s*(gb2312|gbk|gb18030|big5)\b", RegexOptions.IgnoreCase);
}
