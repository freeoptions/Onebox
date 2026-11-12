using System.Text;
using MailKit;

namespace OneBox.Providers;

internal sealed record ImapMessageLocation(string? FolderName, uint? UidValidity, UniqueId Uid)
{
    public string ServerId => FolderName is null ? Uid.Id.ToString() :
        $"junk.{Convert.ToBase64String(Encoding.UTF8.GetBytes(FolderName)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{UidValidity}.{Uid.Id}";

    public static ImapMessageLocation Parse(string serverId)
    {
        if (uint.TryParse(serverId, out var inboxUid) && inboxUid > 0)
            return new(null, null, new UniqueId(inboxUid));
        var partList = serverId.Split('.');
        if (partList.Length != 4 || partList[0] != "junk" || !uint.TryParse(partList[2], out var validity) ||
            !uint.TryParse(partList[3], out var uid) || uid == 0)
            throw new ArgumentException("邮件定位信息无效", nameof(serverId));
        var folder = partList[1].Replace('-', '+').Replace('_', '/');
        folder = Encoding.UTF8.GetString(Convert.FromBase64String(folder.PadRight((folder.Length + 3) / 4 * 4, '=')));
        return new(folder, validity, new UniqueId(uid));
    }
}
