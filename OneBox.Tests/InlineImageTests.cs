using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MimeKit;
using OneBox.App;
using OneBox.Core;
using OneBox.Providers;

namespace OneBox.Tests;

[TestClass]
public sealed class InlineImageTests
{
    [TestMethod]
    public async Task RealMimeImageIsDecodedCachedAndIsolatedFromOtherAccountsAndMessages()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        var accountId = Guid.NewGuid();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        try
        {
            var builder = new BodyBuilder { HtmlBody = "<p>nihao<img src=\"cid:picture%40onebox\"></p><img src=\"https://example.test/remote.png\">" };
            var linkedImage = builder.LinkedResources.Add("picture.png", png);
            linkedImage.ContentId = "picture@onebox";
            builder.Attachments.Add("report.txt", Encoding.UTF8.GetBytes("附件内容"));
            using var original = new MimeMessage { Body = builder.ToMessageBody() };
            using var raw = new MemoryStream();
            await original.WriteToAsync(raw);
            raw.Position = 0;
            using var parsed = await MimeMessage.LoadAsync(raw);
            Assert.IsTrue(InlineImages.HasUnresolvedReferences(parsed.HtmlBody));
            var html = await InlineImages.ResolveAsync(parsed, accountId, "42", directory, CancellationToken.None);
            Assert.IsNotNull(html);
            Assert.IsFalse(InlineImages.HasUnresolvedReferences(html));
            StringAssert.Contains(html, "https://example.test/remote.png");
            var url = Regex.Match(html, @"https://onebox-inline\.invalid/[^""<>]+").Value;
            var imageList = InlineImages.GetImageList(html + html);
            Assert.AreEqual(1, imageList.Count);
            Assert.AreEqual(url, imageList[0].AbsoluteUri);
            using var image = InlineImages.Open(new Uri(url), accountId, "42", directory, out var contentType);
            Assert.IsNotNull(image);
            Assert.AreEqual("image/png", contentType);
            using var bytes = new MemoryStream();
            await image.CopyToAsync(bytes);
            CollectionAssert.AreEqual(png, bytes.ToArray());
            bytes.Position = 0;
            var decoded = BitmapDecoder.Create(bytes, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.AreEqual(1, decoded.Frames[0].PixelWidth);
            var downloadDirectory = Path.Combine(directory, "保存图片");
            var savedPath = await InlineImages.SaveAsync(new Uri(url), accountId, "42", directory, downloadDirectory, CancellationToken.None);
            var secondPath = await InlineImages.SaveAsync(new Uri(url), accountId, "42", directory, downloadDirectory, CancellationToken.None);
            StringAssert.StartsWith(Path.GetFileName(savedPath), "图片-");
            Assert.AreEqual(savedPath, secondPath);
            Assert.AreEqual(1, Directory.GetFiles(downloadDirectory).Length);
            CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(savedPath));
            CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(secondPath));
            File.Delete(savedPath);
            var restoredPath = await InlineImages.SaveAsync(new Uri(url), accountId, "42", directory, downloadDirectory, CancellationToken.None);
            Assert.AreEqual(savedPath, restoredPath);
            CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(restoredPath));
            await Assert.ThrowsAsync<FileNotFoundException>(() => InlineImages.SaveAsync(new Uri(url), Guid.NewGuid(), "42",
                directory, downloadDirectory, CancellationToken.None));
            Assert.IsNull(InlineImages.Open(new Uri(url), Guid.NewGuid(), "42", directory, out _));
            Assert.IsNull(InlineImages.Open(new Uri(url), accountId, "another-message", directory, out _));

            var attachmentPath = Path.Combine(directory, "report.txt");
            await MimeAttachments.SaveAsync(parsed.Attachments.Single(), attachmentPath, CancellationToken.None);
            Assert.AreEqual("附件内容", await File.ReadAllTextAsync(attachmentPath));
            InlineImages.DeleteAccount(accountId, directory);
            Assert.IsNull(InlineImages.Open(new Uri(url), accountId, "42", directory, out _));
            await Assert.ThrowsAsync<IOException>(() => MimeAttachments.SaveAsync(parsed.Attachments.Single(), attachmentPath, CancellationToken.None));
            Assert.AreEqual("附件内容", await File.ReadAllTextAsync(attachmentPath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ForwardedMessageAttachmentCanBeSavedAsEml()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var message = new MimeMessage { Subject = "转发邮件", Body = new TextPart("plain") { Text = "附件中的正文" } };
            using var attachment = new MessagePart("rfc822") { Message = message, ContentDisposition = new ContentDisposition("attachment") };
            var path = Path.Combine(directory, "forwarded.eml");
            await MimeAttachments.SaveAsync(attachment, path, CancellationToken.None);
            using var reloaded = await MimeMessage.LoadAsync(path);
            Assert.AreEqual("转发邮件", reloaded.Subject);
            Assert.IsNotNull(reloaded.TextBody);
            Assert.AreEqual("附件中的正文", reloaded.TextBody.Trim());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void LegacyCidBodyIsRefetchedAndResolvedBodyRetainsCacheAndAttachmentState()
    {
        var message = new MailMessage(Guid.NewGuid(), "1", "INBOX", "测试", "", "", "", DateTimeOffset.UtcNow, "", true, true, false,
            "正文", "<img src=\"cid:picture\">");
        var row = new MessageRow(message, "测试账号");
        Assert.IsFalse(row.HasCachedBody);
        row.CacheBody(message with { HtmlBody = "<img src=\"https://onebox-inline.invalid/cached.png\">", HasAttachments = true });
        Assert.IsTrue(row.HasCachedBody);
        Assert.IsTrue(row.HasAttachments);
        Assert.IsTrue(row.Message.IsRead);
        Assert.IsTrue(row.Message.IsStarred);
    }
}
