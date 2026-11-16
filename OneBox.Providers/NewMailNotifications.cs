using OneBox.Core;

namespace OneBox.Providers;

public static class NewMailNotifications
{
    public static IReadOnlyList<MailMessage> Select(IEnumerable<MailMessage> messageList, SyncCursor? cursor,
        long? currentUidValidity, IReadOnlySet<string> existingIdSet,
        SyncCursor? junkCursor = null, SyncCursor? currentJunkCursor = null)
    {
        if (cursor?.LastSuccess is null) return [];
        var notificationList = new List<MailMessage>();
        foreach (var message in messageList.DistinctBy(message => message.Key))
        {
            if (message.AccountId != cursor.AccountId || message.IsRead || message.FolderId != "INBOX" ||
                existingIdSet.Contains(message.ServerId)) continue;
            if (currentUidValidity is not null)
            {
                var location = ImapMessageLocation.Parse(message.ServerId);
                var previousCursor = location.FolderName is null ? cursor : junkCursor;
                var validity = location.FolderName is null ? currentUidValidity : currentJunkCursor?.UidValidity;
                if (previousCursor?.LastSuccess is null || validity != previousCursor.UidValidity ||
                    previousCursor.HighestUid is not long highestUid || location.Uid.Id <= highestUid) continue;
            }
            else if (message.ReceivedAt < cursor.LastSuccess) continue;
            notificationList.Add(message);
        }
        return notificationList.OrderBy(message => message.ReceivedAt).ToArray();
    }
}
