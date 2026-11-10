using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MimeKit;
using OneBox.Providers;

namespace OneBox.Tests;

[TestClass]
public sealed class EncodingTests
{
    [TestMethod]
    [DataRow("gb2312", 936)]
    [DataRow("gbk", 936)]
    [DataRow("gb18030", 54936)]
    [DataRow("big5", 950)]
    public async Task DeclaredLegacyCharsetHtmlIsDecodedAsChinese(string charset, int codePage)
    {
        _ = new ImapSmtpProvider(OneBox.Core.ProviderKind.Qq);
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage)!;
        var html = $"<html><head><meta charset={charset}></head><body>信用卡</body></html>";
        var raw = $"MIME-Version: 1.0\r\nContent-Type: text/html; charset={charset}\r\nContent-Transfer-Encoding: base64\r\n\r\n" + Convert.ToBase64String(encoding.GetBytes(html));
        using var mime = MimeMessage.Load(new MemoryStream(Encoding.ASCII.GetBytes(raw)));
        var decoded = await InlineImages.ResolveAsync(mime, Guid.NewGuid(), "1", Path.GetTempPath(), CancellationToken.None);
        StringAssert.Contains(decoded, "信用卡");
        Assert.IsTrue(MailEncoding.NeedsReload(html));
        Assert.IsFalse(MailEncoding.NeedsReload(decoded));
    }

    [TestMethod]
    public void CachedLegacyBodyIsReloadedAndUtf8BodyIsPreserved()
    {
        Assert.IsTrue(MailEncoding.NeedsReload("<meta http-equiv=Content-Type content=\"text/html; charset=gbk\">乱码"));
        Assert.IsFalse(MailEncoding.NeedsReload("<meta charset=utf-8>正常正文"));
    }
}
