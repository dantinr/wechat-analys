using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal sealed record VoicePayload(byte[]? Bytes, string Status);

internal static class RawVoiceSource
{
    internal const int MaximumBytes = 32 * 1024 * 1024;

    internal static Dictionary<string, VoicePayload> Read(string root, IReadOnlyList<CaptureRecord> records)
    {
        string directory = Path.Combine(root, "message");
        string[] files = Directory.Exists(directory) ? Directory.GetFiles(directory, "media_*.db")
            .Where(p => Regex.IsMatch(Path.GetFileName(p), @"^media_\d+\.db$", RegexOptions.CultureInvariant)).Order().ToArray() : [];
        if (files.Length == 0) return records.ToDictionary(r => r.Id, _ => new VoicePayload(null, "未找到本地语音库，请先在微信加载语音后重新导出"));
        var keys = RawKeys.Find(files, null, CancellationToken.None);
        try
        {
            return Read(root, records, files, path =>
            {
                byte[] snapshot = CipherPages.Snapshot(path, keys[path], CancellationToken.None);
                try { return MemoryDatabase.Open(snapshot); }
                finally { CryptographicOperations.ZeroMemory(snapshot); }
            });
        }
        finally { foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key); }
    }

    // Database opening is separate so synthetic fixtures exercise the actual matching queries.
    internal static Dictionary<string, VoicePayload> Read(string root, IReadOnlyList<CaptureRecord> records,
        IEnumerable<string> files, Func<string, SqliteConnection> open)
    {
        var result = records.ToDictionary(r => r.Id, _ => new VoicePayload(null, "未找到对应的本地音频，请先在微信加载语音后重新导出"), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var targetIds = records.Select(r => r.TargetId).ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (string file in files)
            {
                using var db = open(file);
                var chats = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                using (var command = db.CreateCommand())
                {
                    command.CommandText = "SELECT rowid,user_name FROM Name2Id";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.IsDBNull(1)) continue;
                        // Target IDs bind both the account directory and the original conversation.
                        // This also works after a binding was removed, without guessing from display names.
                        string target = RawChatSource.TargetIdFor(root, reader.GetString(1));
                        if (!targetIds.Contains(target)) continue;
                        if (!chats.TryGetValue(target, out var ids)) chats[target] = ids = [];
                        ids.Add(reader.GetInt64(0));
                    }
                }
                foreach (var record in records)
                {
                    if (record.Message is not { } message || (message.RawType & 0xffffffff) != 34 ||
                        !chats.TryGetValue(record.TargetId, out var chatIds)) continue;
                    foreach (long chat in chatIds)
                    {
                        using var command = db.CreateCommand();
                        command.CommandText = """
                            SELECT length(voice_data),CASE WHEN length(voice_data) BETWEEN 1 AND $maximum THEN voice_data ELSE NULL END
                            FROM VoiceInfo WHERE chat_name_id=$chat AND local_id=$local AND svr_id=$server AND create_time=$time LIMIT 2
                            """;
                        command.Parameters.AddWithValue("$maximum", MaximumBytes);
                        command.Parameters.AddWithValue("$chat", chat);
                        command.Parameters.AddWithValue("$local", message.LocalId);
                        command.Parameters.AddWithValue("$server", message.ServerId);
                        command.Parameters.AddWithValue("$time", message.SentAt.ToUnixTimeSeconds());
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            if (!matched.Add(record.Id))
                            {
                                Clear(result[record.Id]);
                                result[record.Id] = new(null, "匹配到多条音频，无法确认完整语音，未导出");
                                continue;
                            }
                            result[record.Id] = reader.IsDBNull(0) || reader.GetInt64(0) == 0
                                ? new(null, "本地音频为空，请先在微信加载语音后重新导出")
                                : reader.GetInt64(0) > MaximumBytes
                                    ? new(null, "音频超过单条导出大小限制，未导出")
                                    : new((byte[])reader.GetValue(1), "已读取");
                        }
                    }
                }
            }
            return result;
        }
        catch
        {
            foreach (var payload in result.Values) Clear(payload);
            throw;
        }
    }

    internal static void Clear(VoicePayload payload)
    {
        if (payload.Bytes is { } bytes) CryptographicOperations.ZeroMemory(bytes);
    }
}
