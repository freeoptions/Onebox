using Microsoft.Data.Sqlite;
using OneBox.Core;
using PinyinNet;

namespace OneBox.Storage;

public sealed class SqliteMailStore(string databasePath) : IMailStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true
    }.ToString();

    private SqliteConnection Open() => new(_connectionString);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var existing = File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version > 2) throw new InvalidOperationException("本地数据库由更新版本的 OneBox 创建，无法安全打开");
        if (existing && version < 2)
        {
            var backupPath = databasePath + ".backup." + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss");
            await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            await backup.OpenAsync(cancellationToken);
            connection.BackupDatabase(backup);
        }
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS accounts (
                id TEXT PRIMARY KEY, address TEXT NOT NULL, display_name TEXT NOT NULL,
                provider INTEGER NOT NULL, status INTEGER NOT NULL, last_error TEXT
            );
            CREATE TABLE IF NOT EXISTS folders (
                account_id TEXT NOT NULL, server_id TEXT NOT NULL, name TEXT NOT NULL,
                kind INTEGER NOT NULL, uid_validity INTEGER, highest_uid INTEGER,
                PRIMARY KEY(account_id,server_id),
                FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS messages (
                account_id TEXT NOT NULL, server_id TEXT NOT NULL, folder_id TEXT NOT NULL,
                subject TEXT NOT NULL, from_name TEXT NOT NULL, from_address TEXT NOT NULL,
                to_addresses TEXT NOT NULL, received_at TEXT NOT NULL, preview TEXT NOT NULL,
                is_read INTEGER NOT NULL, is_starred INTEGER NOT NULL, has_attachments INTEGER NOT NULL,
                text_body TEXT, html_body TEXT, search_pinyin TEXT NOT NULL DEFAULT '',
                search_initials TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(account_id,server_id),
                FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_messages_date ON messages(received_at DESC);
            CREATE TABLE IF NOT EXISTS attachments (
                account_id TEXT NOT NULL, message_server_id TEXT NOT NULL, part_id TEXT NOT NULL,
                file_name TEXT NOT NULL, content_type TEXT NOT NULL, size INTEGER NOT NULL,
                PRIMARY KEY(account_id,message_server_id,part_id),
                FOREIGN KEY(account_id,message_server_id) REFERENCES messages(account_id,server_id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS sync_cursors (
                account_id TEXT NOT NULL, folder_id TEXT NOT NULL, gmail_history_id TEXT,
                uid_validity INTEGER, highest_uid INTEGER, last_success TEXT,
                PRIMARY KEY(account_id,folder_id),
                FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS pending_actions (
                id TEXT PRIMARY KEY, account_id TEXT NOT NULL, message_server_id TEXT NOT NULL,
                kind INTEGER NOT NULL, created_at TEXT NOT NULL, attempts INTEGER NOT NULL,
                FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(messages)";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) names.Add(reader.GetString(1));
        if (!names.Contains("search_pinyin"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE messages ADD COLUMN search_pinyin TEXT NOT NULL DEFAULT ''";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!names.Contains("search_initials"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE messages ADD COLUMN search_initials TEXT NOT NULL DEFAULT ''";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        var updatedVersion = connection.CreateCommand();
        updatedVersion.CommandText = "PRAGMA user_version=2";
        await updatedVersion.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,address,display_name,provider,status,last_error FROM accounts ORDER BY address";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<Account>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new Account(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                (ProviderKind)reader.GetInt32(3), (ConnectionStatus)reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }

    public async Task UpsertAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO accounts VALUES($id,$address,$name,$provider,$status,$error)
            ON CONFLICT(id) DO UPDATE SET address=$address,display_name=$name,
            provider=$provider,status=$status,last_error=$error
            """;
        command.Parameters.AddWithValue("$id", account.Id.ToString("N"));
        command.Parameters.AddWithValue("$address", account.Address);
        command.Parameters.AddWithValue("$name", account.DisplayName);
        command.Parameters.AddWithValue("$provider", (int)account.Provider);
        command.Parameters.AddWithValue("$status", (int)account.Status);
        command.Parameters.AddWithValue("$error", (object?)account.LastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; DELETE FROM accounts WHERE id=$id";
        command.Parameters.AddWithValue("$id", accountId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailMessage>> GetMessagesAsync(Guid? accountId, string? folderId,
        string? query, int limit = 200, bool starredOnly = false, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT account_id,server_id,folder_id,subject,from_name,from_address,to_addresses,
                   received_at,preview,is_read,is_starred,has_attachments,text_body,html_body
            FROM messages
            WHERE ($account IS NULL OR account_id=$account)
              AND ($folder IS NULL OR folder_id=$folder)
              AND ($starredOnly=0 OR is_starred=1)
              AND ($query IS NULL OR subject LIKE $query OR from_name LIKE $query
                   OR from_address LIKE $query OR to_addresses LIKE $query OR text_body LIKE $query
                   OR search_pinyin LIKE $pinyinQuery OR search_initials LIKE $pinyinQuery)
            ORDER BY received_at DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$account", accountId?.ToString("N") is string id ? id : DBNull.Value);
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$starredOnly", starredOnly);
        command.Parameters.AddWithValue("$query", string.IsNullOrWhiteSpace(query) ? DBNull.Value : $"%{query}%");
        command.Parameters.AddWithValue("$pinyinQuery", string.IsNullOrWhiteSpace(query)
            ? DBNull.Value : $"%{MailRules.NormalizeSearch(query)}%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<MailMessage>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new MailMessage(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                DateTimeOffset.Parse(reader.GetString(7)), reader.GetString(8), reader.GetBoolean(9),
                reader.GetBoolean(10), reader.GetBoolean(11), reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        return result;
    }

    public async Task UpsertMessagesAsync(IEnumerable<MailMessage> messages, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var message in messages)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO messages VALUES($account,$server,$folder,$subject,$fromName,$fromAddress,
                    $to,$date,$preview,$read,$starred,$attachments,$text,$html,$pinyin,$initials)
                ON CONFLICT(account_id,server_id) DO UPDATE SET
                    folder_id=$folder,subject=$subject,from_name=$fromName,from_address=$fromAddress,
                    to_addresses=$to,received_at=$date,preview=$preview,is_read=$read,
                    is_starred=$starred,has_attachments=$attachments,
                    text_body=COALESCE($text,messages.text_body),
                    html_body=COALESCE($html,messages.html_body),search_pinyin=$pinyin,
                    search_initials=$initials
                """;
            command.Parameters.AddWithValue("$account", message.AccountId.ToString("N"));
            command.Parameters.AddWithValue("$server", message.ServerId);
            command.Parameters.AddWithValue("$folder", message.FolderId);
            command.Parameters.AddWithValue("$subject", message.Subject);
            command.Parameters.AddWithValue("$fromName", message.FromName);
            command.Parameters.AddWithValue("$fromAddress", message.FromAddress);
            command.Parameters.AddWithValue("$to", message.ToAddresses);
            command.Parameters.AddWithValue("$date", message.ReceivedAt.ToString("O"));
            command.Parameters.AddWithValue("$preview", message.Preview);
            command.Parameters.AddWithValue("$read", message.IsRead);
            command.Parameters.AddWithValue("$starred", message.IsStarred);
            command.Parameters.AddWithValue("$attachments", message.HasAttachments);
            command.Parameters.AddWithValue("$text", (object?)message.TextBody ?? DBNull.Value);
            command.Parameters.AddWithValue("$html", (object?)message.HtmlBody ?? DBNull.Value);
            var searchable = string.Join(' ', message.FromName, message.FromAddress, message.ToAddresses,
                message.Subject, message.TextBody ?? "");
            command.Parameters.AddWithValue("$pinyin", MailRules.NormalizeSearch(
                PinyinConvert.GetPinyin(searchable).Replace("|", "")));
            command.Parameters.AddWithValue("$initials", MailRules.NormalizeSearch(
                PinyinConvert.GetPinyinFirstLetter(searchable)));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetFolderAsync(Guid accountId, string folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            DELETE FROM messages WHERE account_id=$account AND folder_id=$folder;
            DELETE FROM sync_cursors WHERE account_id=$account AND folder_id=$folder;
            """;
        command.Parameters.AddWithValue("$account", accountId.ToString("N"));
        command.Parameters.AddWithValue("$folder", folderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveMissingMessagesAsync(Guid accountId, string folderId, IReadOnlySet<string> serverIdSet,
        DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var query = connection.CreateCommand();
        query.Transaction = (SqliteTransaction)transaction;
        query.CommandText = "SELECT server_id,received_at FROM messages WHERE account_id=$account AND folder_id=$folder";
        query.Parameters.AddWithValue("$account", accountId.ToString("N"));
        query.Parameters.AddWithValue("$folder", folderId);
        var missingIdList = new List<string>();
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                if (DateTimeOffset.Parse(reader.GetString(1)) >= since && !serverIdSet.Contains(reader.GetString(0)))
                    missingIdList.Add(reader.GetString(0));
        foreach (var id in missingIdList)
        {
            var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM pending_actions WHERE account_id=$account AND message_server_id=$id;
                DELETE FROM messages WHERE account_id=$account AND server_id=$id AND folder_id=$folder;
                """;
            delete.Parameters.AddWithValue("$account", accountId.ToString("N"));
            delete.Parameters.AddWithValue("$id", id);
            delete.Parameters.AddWithValue("$folder", folderId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SyncCursor?> GetCursorAsync(Guid accountId, string folderId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT gmail_history_id,uid_validity,highest_uid,last_success FROM sync_cursors WHERE account_id=$account AND folder_id=$folder";
        command.Parameters.AddWithValue("$account", accountId.ToString("N"));
        command.Parameters.AddWithValue("$folder", folderId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncCursor(accountId, folderId, reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3)));
    }

    public async Task SaveCursorAsync(SyncCursor cursor, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_cursors VALUES($account,$folder,$history,$validity,$uid,$date)
            ON CONFLICT(account_id,folder_id) DO UPDATE SET gmail_history_id=$history,
                uid_validity=$validity,highest_uid=$uid,last_success=$date
            """;
        command.Parameters.AddWithValue("$account", cursor.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$folder", cursor.FolderId);
        command.Parameters.AddWithValue("$history", (object?)cursor.GmailHistoryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$validity", (object?)cursor.UidValidity ?? DBNull.Value);
        command.Parameters.AddWithValue("$uid", (object?)cursor.HighestUid ?? DBNull.Value);
        command.Parameters.AddWithValue("$date", cursor.LastSuccess?.ToString("O") is string date ? date : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnqueueAsync(PendingAction action, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO pending_actions VALUES($id,$account,$message,$kind,$date,$attempts)";
        command.Parameters.AddWithValue("$id", action.Id.ToString("N"));
        command.Parameters.AddWithValue("$account", action.AccountId.ToString("N"));
        command.Parameters.AddWithValue("$message", action.MessageServerId);
        command.Parameters.AddWithValue("$kind", (int)action.Kind);
        command.Parameters.AddWithValue("$date", action.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$attempts", action.Attempts);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingAction>> GetPendingAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,message_server_id,kind,created_at,attempts FROM pending_actions WHERE account_id=$account ORDER BY created_at";
        command.Parameters.AddWithValue("$account", accountId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PendingAction>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new PendingAction(Guid.Parse(reader.GetString(0)), accountId, reader.GetString(1),
                (PendingActionKind)reader.GetInt32(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetInt32(4)));
        return result;
    }

    public async Task CompletePendingAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        await using var connection = Open();
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_actions WHERE id=$id";
        command.Parameters.AddWithValue("$id", actionId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
