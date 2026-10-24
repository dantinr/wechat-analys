using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace WeChatNotes;

public sealed class Store : IDisposable
{
    public string Root { get; }
    private readonly SqliteConnection db;

    public Store(string root) : this(root, false) { }

    internal Store(string root, bool readOnly)
    {
        Root = Path.GetFullPath(root);
        if (!readOnly) Directory.CreateDirectory(Root);
        db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Root, "records.sqlite3"),
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate
        }.ToString());
        db.Open();
        db.CreateFunction<string, string, bool>("wechat_contains", (text, term) => text.Contains(term, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        if (readOnly) return;
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS targets(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS target_sources(id TEXT PRIMARY KEY, source_root TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS closed_conversations(id TEXT PRIMARY KEY);
            INSERT OR IGNORE INTO target_sources(id,source_root)
                SELECT id,json_extract(json,'$.SourceRoot') FROM targets WHERE json_extract(json,'$.SourceRoot') IS NOT NULL;
            CREATE TABLE IF NOT EXISTS captures(id TEXT PRIMARY KEY, target_id TEXT NOT NULL, observed_at TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS captures_target_time ON captures(target_id, observed_at);
            CREATE INDEX IF NOT EXISTS captures_target_order ON captures(target_id);
            CREATE TABLE IF NOT EXISTS message_fingerprints(id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public List<Target> Targets() => Read<Target>("SELECT json FROM targets ORDER BY rowid");
    internal bool HasVoiceRecords(string targetId)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM captures WHERE target_id=$target AND (json_extract(json,'$.Message.RawType') & 4294967295)=34)";
        command.Parameters.AddWithValue("$target", targetId);
        return (long)command.ExecuteScalar()! != 0;
    }
    public List<CaptureRecord> Captures(int? limit = null, string? targetId = null)
    {
        string filter = targetId == null ? "" : " WHERE target_id=$target";
        return Read<CaptureRecord>(limit.HasValue
            ? $"SELECT json FROM (SELECT rowid,json FROM captures{filter} ORDER BY rowid DESC LIMIT $limit) ORDER BY rowid"
            : $"SELECT json FROM captures{filter} ORDER BY rowid", limit, targetId);
    }
    public List<(string Id, string Name)> CaptureConversations()
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.target_id,json_extract(c.json,'$.TargetName') FROM captures c
            JOIN (SELECT target_id,MAX(rowid) latest FROM captures GROUP BY target_id) last ON c.rowid=last.latest
            WHERE NOT EXISTS (SELECT 1 FROM closed_conversations hidden WHERE hidden.id=c.target_id)
            ORDER BY c.rowid
            """;
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }
    internal CapturePage CapturePage(int pageSize, long pageNumber = 1, string? targetId = null, string search = "", string? anchorId = null)
    {
        if (pageSize is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));
        bool filtering = !string.IsNullOrWhiteSpace(search);
        string scope = targetId == null ? "1=1" : "target_id=$target";
        string filter = scope + (filtering ? """
             AND wechat_contains(
                COALESCE(json_extract(json,'$.Text'),'') || char(10) ||
                COALESCE(json_extract(json,'$.TargetName'),'') || char(10) ||
                COALESCE(json_extract(json,'$.Message.Sender'),'') || char(10) ||
                COALESCE(json_extract(json,'$.Message.SenderId'),'') || char(10) ||
                COALESCE(json_extract(json,'$.Message.GroupNickname'),''),$search)
            """ : "");
        SqliteCommand Command(string sql)
        {
            var command = db.CreateCommand(); command.CommandText = sql;
            if (targetId != null) command.Parameters.AddWithValue("$target", targetId);
            if (filtering) command.Parameters.AddWithValue("$search", search);
            return command;
        }
        long Count(string predicate)
        {
            using var command = Command($"SELECT COUNT(*) FROM captures WHERE {predicate}");
            return (long)command.ExecuteScalar()!;
        }
        long total = Count(scope), matches = filtering ? Count(filter) : total;
        long pageCount = matches == 0 ? 1 : (matches - 1) / pageSize + 1;
        if (anchorId != null)
        {
            using var anchor = Command($"SELECT rowid FROM captures WHERE {filter} AND id=$anchor");
            anchor.Parameters.AddWithValue("$anchor", anchorId);
            if (anchor.ExecuteScalar() is long rowid)
            {
                using var rank = Command($"SELECT COUNT(*) FROM captures WHERE {filter} AND rowid>$rowid");
                rank.Parameters.AddWithValue("$rowid", rowid);
                pageNumber = (long)rank.ExecuteScalar()! / pageSize + 1;
            }
        }
        pageNumber = Math.Clamp(pageNumber, 1, pageCount);
        using var query = Command($"SELECT json FROM captures WHERE {filter} ORDER BY rowid DESC LIMIT $limit OFFSET $offset");
        query.Parameters.AddWithValue("$limit", pageSize); query.Parameters.AddWithValue("$offset", (pageNumber - 1) * pageSize);
        using var reader = query.ExecuteReader(); var records = new List<CaptureRecord>();
        while (reader.Read()) records.Add(JsonSerializer.Deserialize<CaptureRecord>(reader.GetString(0))!);
        return new(total, matches, pageNumber, pageCount, records);
    }
    public void SaveTarget(Target target)
    {
        Execute("INSERT OR REPLACE INTO targets(id,json) VALUES($id,$json); DELETE FROM closed_conversations WHERE id=$id", target.Id, target);
        if (!string.IsNullOrEmpty(target.SourceRoot))
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO target_sources(id,source_root) VALUES($id,$root)";
            command.Parameters.AddWithValue("$id", target.Id); command.Parameters.AddWithValue("$root", target.SourceRoot); command.ExecuteNonQuery();
        }
    }
    internal Dictionary<string, string> TargetSources()
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT id,source_root FROM target_sources";
        using var reader = command.ExecuteReader(); var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read()) sources[reader.GetString(0)] = reader.GetString(1);
        return sources;
    }
    public void RemoveTarget(string id) => Execute("DELETE FROM targets WHERE id=$id", id, null);

    internal void CloseConversation(string id)
    {
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO closed_conversations(id) VALUES($id); DELETE FROM targets WHERE id=$id";
        command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); transaction.Commit();
    }

    public void Add(CaptureRecord record, Bitmap crop)
    {
        string image = ImageFile(record.ImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);
        crop.Save(image, System.Drawing.Imaging.ImageFormat.Png);
        try
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO captures(id,target_id,observed_at,json) VALUES($id,$target,$time,$json)";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$target", record.TargetId);
            command.Parameters.AddWithValue("$time", record.ObservedAt.ToString("O"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record));
            command.ExecuteNonQuery();
        }
        catch { File.Delete(image); throw; }
    }

    public int AddMessages(IEnumerable<CaptureRecord> records)
    {
        using var transaction = db.BeginTransaction();
        int count = 0;
        foreach (var record in records)
        {
            string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { record.Text, record.Message }))));
            using (var previous = db.CreateCommand())
            {
                previous.Transaction = transaction;
                previous.CommandText = "SELECT fingerprint FROM message_fingerprints WHERE id=$id";
                previous.Parameters.AddWithValue("$id", record.Id);
                if ((string?)previous.ExecuteScalar() == fingerprint) continue;
            }
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO captures(id,target_id,observed_at,json) VALUES($id,$target,$time,$json)
                ON CONFLICT(id) DO UPDATE SET observed_at=excluded.observed_at,json=excluded.json;
                INSERT INTO message_fingerprints(id,fingerprint) VALUES($id,$fingerprint)
                ON CONFLICT(id) DO UPDATE SET fingerprint=excluded.fingerprint;
                """;
            command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$target", record.TargetId);
            command.Parameters.AddWithValue("$time", record.ObservedAt.ToString("O"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record));
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.ExecuteNonQuery(); count++;
        }
        transaction.Commit(); return count;
    }

    public string ImageFile(string relative)
    {
        string result = Path.GetFullPath(Path.Combine(Root, relative));
        if (!result.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("截图路径不在数据目录内。");
        return result;
    }

    private List<T> Read<T>(string sql, int? limit = null, string? targetId = null)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        if (limit.HasValue) command.Parameters.AddWithValue("$limit", limit.Value);
        if (targetId != null) command.Parameters.AddWithValue("$target", targetId);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0))!);
        return result;
    }
    private void Execute(string sql, string id, object? item)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        if (item != null) command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item));
        command.ExecuteNonQuery();
    }
    public void Dispose() => db.Dispose();
}

internal sealed record CapturePage(long TotalCount, long MatchCount, long PageNumber, long PageCount, List<CaptureRecord> Records);
