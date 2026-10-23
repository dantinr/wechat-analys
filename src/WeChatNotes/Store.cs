using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace WeChatNotes;

public sealed class Store : IDisposable
{
    public string Root { get; }
    private readonly SqliteConnection db;

    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "records.sqlite3") }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS targets(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS captures(id TEXT PRIMARY KEY, target_id TEXT NOT NULL, observed_at TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS captures_target_time ON captures(target_id, observed_at);
            CREATE TABLE IF NOT EXISTS requirements(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS message_fingerprints(id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public List<Target> Targets() => Read<Target>("SELECT json FROM targets ORDER BY rowid");
    public List<CaptureRecord> Captures(int? limit = null) => Read<CaptureRecord>(limit.HasValue
        ? "SELECT json FROM (SELECT rowid,json FROM captures ORDER BY rowid DESC LIMIT $limit) ORDER BY rowid" : "SELECT json FROM captures ORDER BY rowid", limit);
    public List<Requirement> Requirements() => Read<Requirement>("SELECT json FROM requirements ORDER BY rowid");
    public void SaveTarget(Target target) => Execute("INSERT OR REPLACE INTO targets(id,json) VALUES($id,$json)", target.Id, target);
    public void RemoveTarget(string id) => Execute("DELETE FROM targets WHERE id=$id", id, null);
    public void SaveRequirement(Requirement item) => Execute("INSERT OR REPLACE INTO requirements(id,json) VALUES($id,$json)", item.Id, item);

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

    private List<T> Read<T>(string sql, int? limit = null)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        if (limit.HasValue) command.Parameters.AddWithValue("$limit", limit.Value);
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
