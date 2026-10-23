using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace WeChatNotes;

internal static class RawProbe
{
    internal static void Run(string report, string? conversation)
    {
        report = Path.GetFullPath(report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var log = new List<object>();
        Dictionary<string, byte[]> keys = [];
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "xwechat_files");
            string storage = Directory.GetDirectories(root).Select(d => Path.Combine(d, "db_storage")).First(Directory.Exists);
            string[] files = [Path.Combine(storage, "session", "session.db"), Path.Combine(storage, "contact", "contact.db"), .. Directory.GetFiles(Path.Combine(storage, "message"), "message_*.db").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), @"^message_\d+\.db$"))];
            keys = RawKeys.Find(files, s => { log.Add(new { status = s }); File.WriteAllText(report, JsonSerializer.Serialize(log)); }, CancellationToken.None);
            foreach (var file in files)
            {
                byte[] bytes = CipherPages.Snapshot(file, keys[file], CancellationToken.None);
                using var db = MemoryDatabase.Open(bytes); CryptographicOperations.ZeroMemory(bytes);
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA quick_check(1)";
                string? check = command.ExecuteScalar()?.ToString();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                var schemas = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) schemas.Add(reader.GetString(0));
                log.Add(new { database = Path.GetFileName(file), check, tables = schemas.Count, schema = schemas.Where(n => !n.StartsWith("Msg_")).ToArray() });
                if (Path.GetFileName(file) == "contact.db")
                    foreach (string table in new[] { "chat_room", "chat_room_info_detail", "chatroom_member" }.Where(schemas.Contains))
                    {
                        command.CommandText = $"PRAGMA table_info(\"{table}\")";
                        var columns = new List<object>();
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) columns.Add(new { name = reader.GetString(1), type = reader.GetString(2) });
                        log.Add(new { table, columns });
                    }
                if (Path.GetFileName(file) == "contact.db")
                {
                    command.CommandText = "SELECT ext_buffer FROM chat_room WHERE length(ext_buffer)>0";
                    int parsed = 0, failed = 0, nicknames = 0;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.GetValue(0) is byte[] blob && GroupNicknames.TryParse(blob, out var members))
                        { parsed++; nicknames += members.Count; }
                        else failed++;
                    }
                    log.Add(new { roomBuffersParsed = parsed, roomBuffersUnsupported = failed, groupNicknames = nicknames });
                }
                if (Path.GetFileName(file) == "session.db")
                {
                    command.CommandText = "SELECT COUNT(*) FROM SessionTable";
                    log.Add(new { sessions = Convert.ToInt64(command.ExecuteScalar()) });
                }
            }
            using (var source = new RawChatSource(storage))
            {
                var sessions = source.Sessions(null, CancellationToken.None);
                var session = sessions.FirstOrDefault(s => s.DisplayName == conversation);
                if (session != null)
                {
                    var target = new Target("probe-selected-group", session.DisplayName, "", 0,0,0,0,0,0,storage,session.Username,0);
                    var records = source.Read([target], CancellationToken.None);
                    log.Add(new { selectedConversationFound = true, messagesRead = records.Count, textMessages = records.Count(r => (r.Message!.RawType & 0xffffffff) == 1), originalTextPresent = records.Any(r => r.Message!.RawContent.Length > 0), senderResolved = records.Count(r => r.Message!.SenderId.Length > 0), groupNicknamesResolved = records.Count(r => !string.IsNullOrEmpty(r.Message!.GroupNickname)) });
                }
                else if (conversation != null) log.Add(new { selectedConversationFound = false });
            }
            log.Add(new { success = true });
        }
        catch (Exception ex)
        {
            // Exception messages may contain account directories; reports are safe to inspect first.
            log.Add(new { success = false, error = "连接诊断未完成，请在程序界面查看连接提示。", type = ex.GetType().Name, code = ex.HResult });
            Environment.ExitCode = 1;
        }
        finally
        {
            foreach (var key in keys.Values) CryptographicOperations.ZeroMemory(key);
            File.WriteAllText(report, JsonSerializer.Serialize(log, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
