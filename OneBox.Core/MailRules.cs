using System.Globalization;
using System.Text;

namespace OneBox.Core;

public static class MailRules
{
    public static IReadOnlyList<MailMessage> UnifiedInbox(IEnumerable<MailMessage> messages) =>
        messages.Where(message => message.FolderId.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            .GroupBy(message => message.Key, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(message => message.ReceivedAt).First())
            .OrderByDescending(message => message.ReceivedAt).ToArray();

    public static bool IsFromAccount(Account account, OutgoingMessage message) =>
        account.Id == message.AccountId;

    public static string SafeAttachmentName(string name)
    {
        var leaf = Path.GetFileName(name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(leaf.Where(ch => !invalid.Contains(ch) && !char.IsControl(ch)).ToArray()).Trim();
        return clean is "" or "." or ".." ? "附件" : clean;
    }

    public static string AvailableAttachmentPath(string directory, string name)
    {
        var safe = SafeAttachmentName(name);
        var candidate = Path.Combine(directory, safe);
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var number = 1; File.Exists(candidate); number++)
            candidate = Path.Combine(directory, $"{stem} ({number}){ext}");
        return candidate;
    }

    public static string NormalizeSearch(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (!char.IsWhiteSpace(ch)) builder.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
