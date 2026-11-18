using System.Net;
using System.Text.RegularExpressions;
using OneBox.Core;

namespace OneBox.Providers;

public static class MailVerificationCode
{
    private const string Label = @"(?:(?:登录|注册)?验证码|校验码|动态码|确认码|一次性密码|(?<![a-zA-Z0-9_])(?:verification\s+code|verify\s+code|security\s+code|confirmation\s+code|login\s+code|sign[- ]in\s+code|one[- ]time\s+(?:code|password|passcode)|otp|pin)(?![a-zA-Z0-9_]))";
    private const string Code = @"(?<![\p{Nd}a-zA-Z_])(?<![\p{Nd}a-zA-Z][./-])(?=[a-zA-Z]{0,7}[0-9０-９])(?<code>[0-9０-９a-zA-Z]{4,8})(?![\p{Nd}a-zA-Z_]|[./-][\p{Nd}a-zA-Z])";
    private const string Separator = @"[\s:：=*【】\[\]（）()]{0,16}";
    private static readonly Regex LabelFirst = new(Label + Separator + @"(?:(?:为|是|is\b)" + Separator + ")?" + Code,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CodeFirst = new(Code + Separator + @"(?:是(?:您|你)?的?|is\s+(?:your|the))\s*" + Label,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Link = new(@"(?:https?://|www\.)[^\s<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VerificationMail = new(Label + @"|邮箱验证|邮件验证|验证邮箱|验证邮件|\b(?:email verification|verify your email)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsLikelyVerificationMail(MailMessage message) =>
        VerificationMail.IsMatch(message.Subject) || VerificationMail.IsMatch(message.Preview);

    public static (string? Code, bool Ambiguous) Find(MailMessage message)
    {
        var codeSet = new HashSet<string>(StringComparer.Ordinal);
        var body = DeepSeekTranslator.GetBody(message, includeLinkTargets: false);
        foreach (var field in new[] { message.Subject, body.Length > 0 ? body : WebUtility.HtmlDecode(message.Preview) })
        {
            var text = Link.Replace(field, " ");
            foreach (var match in LabelFirst.Matches(text).Concat(CodeFirst.Matches(text)))
            {
                var code = string.Concat(match.Groups["code"].Value.Select(character =>
                    character is >= '０' and <= '９' ? (char)(character - '０' + '0') : character));
                codeSet.Add(code);
                if (codeSet.Count > 1) return (null, true);
            }
        }
        return (codeSet.SingleOrDefault(), false);
    }
}
