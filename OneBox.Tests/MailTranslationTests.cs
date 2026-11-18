using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;
using OneBox.Storage;

namespace OneBox.Tests;

[TestClass]
public sealed class MailTranslationTests
{
    private static MailMessage Message() => new(Guid.NewGuid(), "INBOX:123:42", "INBOX", "Invoice overdue",
        "MassiveGRID", "billing@example.com", "receiver@example.com", DateTimeOffset.UtcNow,
        "Preview", false, false, false, "Amount: $9.99 USD");

    [DataRow("Invoice overdue", "Please update your billing details at https://example.com/中文", false)]
    [DataRow("Invoice overdue", "Please update your billing details at https://example.com/pay.", true)]
    [DataRow("滴滴滴AI｜免费周卡可再领一次", "现有免费周卡增加一次领取机会。", false)]
    [DataRow("Payment received", "您的付款已到账。", false)]
    [DataRow("付款成功", "Thank you for your payment.", false)]
    [DataRow("Bonjour", "Votre dossier est disponible.", false)]
    [DataRow("2026-11-18", "https://example.com/verification", false)]
    [TestMethod]
    public void TranslationAvailabilityRequiresEnglishMail(string subject, string body, bool expected)
    {
        Assert.AreEqual(expected, DeepSeekTranslator.CanTranslate(Message() with { Subject = subject, TextBody = body }));
    }

    [TestMethod]
    public async Task HtmlTranslationChecksRenderedWordsAndRejectsMixedContentBeforeSending()
    {
        var message = Message() with
        {
            HtmlBody = "<head><style>/* 中文注释 */</style></head><body><p>Please verify your account.</p></body>"
        };
        Assert.IsTrue(DeepSeekTranslator.CanTranslate(message));
        Assert.IsFalse(DeepSeekTranslator.CanTranslate(message with { HtmlBody = "<p>&#20013;&#25991;</p>" }));
        using var translator = new DeepSeekTranslator(new MailProxy());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => translator.TranslateAsync(message.Subject, "", "", ["Please verify your account.", "验证邮箱"]));
    }

    [TestMethod]
    public async Task SavedTranslationSurvivesNewCacheInstanceAndFlagChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-translation-test-{Guid.NewGuid():N}");
        try
        {
            var message = Message();
            var key = MailTranslationCache.GetKey(message);
            var translation = new MailTranslation("账单逾期", "金额：$9.99 USD");
            var cache = new MailTranslationCache(directory);
            Assert.IsNull(await cache.ReadAsync(key));
            await cache.SaveAsync(key, translation);
            var reloaded = new MailTranslationCache(directory);
            var changedFlags = message with { IsRead = true, IsStarred = true };
            Assert.AreEqual(key, MailTranslationCache.GetKey(changedFlags));
            Assert.AreEqual(translation, await reloaded.ReadAsync(MailTranslationCache.GetKey(changedFlags)));
            Assert.IsNull(await reloaded.ReadAsync(MailTranslationCache.GetKey(message with { Subject = "Paid" })));
            Assert.IsNull(await reloaded.ReadAsync(MailTranslationCache.GetKey(message with { TextBody = "Amount: $20 USD" })));
            Assert.AreEqual("Invoice overdue", message.Subject);
            Assert.AreEqual("Amount: $9.99 USD", message.TextBody);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task LegacyCacheRemainsReadableAndStyledTextPersistsWithoutChangingSourceKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"OneBox-translation-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var message = Message() with { HtmlBody = "<p>Amount: <b>$9.99 USD</b></p>" };
            var key = MailTranslationCache.GetKey(message);
            await File.WriteAllTextAsync(Path.Combine(directory, key + ".json"), """{"Subject":"旧标题","Body":"旧纯文本译文"}""");
            var cache = new MailTranslationCache(directory);
            var legacy = await cache.ReadAsync(key);
            Assert.AreEqual("旧纯文本译文", legacy!.Body);
            Assert.IsNull(legacy.HtmlTextList, "旧缓存应被识别，不能静默触发重新翻译");
            await cache.SaveAsync(key, new MailTranslation("账单", "金额：$9.99 USD", new[] { "金额：", "$9.99 USD" }));
            var reloaded = await new MailTranslationCache(directory).ReadAsync(key);
            CollectionAssert.AreEqual(new[] { "金额：", "$9.99 USD" }, reloaded!.HtmlTextList!.ToArray());
            Assert.AreEqual(key, MailTranslationCache.GetKey(message));
            Assert.AreEqual("<p>Amount: <b>$9.99 USD</b></p>", message.HtmlBody);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public void CacheSeparatesAccountsFoldersAndHtmlChanges()
    {
        var message = Message() with { TextBody = "", HtmlBody = "<p>Invoice</p>" };
        var key = MailTranslationCache.GetKey(message);
        Assert.AreNotEqual(key, MailTranslationCache.GetKey(message with { AccountId = Guid.NewGuid() }));
        Assert.AreNotEqual(key, MailTranslationCache.GetKey(message with { FolderId = "JUNK" }));
        Assert.AreNotEqual(key, MailTranslationCache.GetKey(message with { HtmlBody = "<p>Paid</p>" }));
        Assert.AreEqual(key, MailTranslationCache.GetKey(message with { FromName = "Changed", ToAddresses = "other@example.com" }));
    }

    [TestMethod]
    public void TranslationUsesCompleteBodyAndStripsHtmlWhenPlainTextIsMissing()
    {
        var message = Message();
        Assert.AreEqual(message.TextBody, DeepSeekTranslator.GetBody(message));
        var body = DeepSeekTranslator.GetBody(message with
        {
            TextBody = "", HtmlBody = "<p>Amount: $9.99 USD</p><p>Invoice: 2017256</p>"
        });
        StringAssert.Contains(body, "Amount: $9.99 USD");
        StringAssert.Contains(body, "Invoice: 2017256");
        Assert.IsFalse(body.Contains("<p>", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains(message.FromAddress, StringComparison.Ordinal));
        Assert.IsFalse(body.Contains(message.Preview, StringComparison.Ordinal));
    }
}
