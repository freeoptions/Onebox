using Microsoft.VisualStudio.TestTools.UnitTesting;
using OneBox.Core;
using OneBox.Providers;

namespace OneBox.Tests;

[TestClass]
public sealed class MailVerificationCodeTests
{
    [DataRow("验证码： 012345", "012345")]
    [DataRow("Your verification code is 654321", "654321")]
    [DataRow("123456 is your one-time passcode", "123456")]
    [DataRow("【１２３４５６】是您的登录验证码", "123456")]
    [DataRow("短信校验码为\n1234", "1234")]
    [DataRow("OTP: 12345678", "12345678")]
    [TestMethod]
    public void FindsCodeNearExplicitLabel(string text, string expected)
    {
        Assert.AreEqual((expected, false), MailVerificationCode.Find(Message() with { Subject = text }));
        Assert.AreEqual((expected, false), MailVerificationCode.Find(Message() with { Preview = text }));
    }

    [DataRow("ZIP code: 90210")]
    [DataRow("Order code: 123456")]
    [DataRow("OTP: 123456789")]
    [DataRow("OTP: prefix123456")]
    [DataRow("The verification code expires soon.")]
    [DataRow("OTP: 123456-7890")]
    [DataRow("123456")]
    [DataRow("https://example.com/?otp=123456")]
    [TestMethod]
    public void IgnoresUnrelatedOrPartialNumbers(string text)
    {
        Assert.AreEqual(((string?)null, false), MailVerificationCode.Find(Message() with { Preview = text }));
    }

    [TestMethod]
    public void ReadsHtmlTextWithoutReadingLinkTargetsOrScript()
    {
        var message = Message() with
        {
            HtmlBody = "<script>OTP: 999999</script><p>验证码：&nbsp;<a href='https://example.com/?otp=888888'><b>12</b>3456</a></p>",
            Preview = "验证码：111111"
        };

        Assert.AreEqual(("123456", false), MailVerificationCode.Find(message));
    }

    [TestMethod]
    public void FindsMixedCaseCodeInVerificationEmailBody()
    {
        var message = Message() with
        {
            Subject = "邮箱验证邮件",
            HtmlBody = "<p>您的验证码为: <strong>aB456c</strong></p><p>验证码10分钟内有效。</p>"
        };

        Assert.AreEqual(("aB456c", false), MailVerificationCode.Find(message));
    }

    [TestMethod]
    public void RepeatedCodesAreAllowedAndConflictingCodesAreAmbiguous()
    {
        var message = Message() with { Subject = "OTP: 123456", TextBody = "验证码：123456" };

        Assert.AreEqual(("123456", false), MailVerificationCode.Find(message));
        Assert.AreEqual(((string?)null, true), MailVerificationCode.Find(message with { TextBody = "验证码：654321" }));
    }

    private static MailMessage Message() => new(Guid.Empty, "1", "INBOX", "", "", "", "",
        DateTimeOffset.UnixEpoch, "", false, false, false);
}
