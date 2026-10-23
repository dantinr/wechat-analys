using System.Text;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal static class GroupNicknames
{
    // WeChat 4.1 contact.db: chat_room.ext_buffer contains repeated field-1 members;
    // each member has field 1 = username and optional field 2 = nickname in this room.
    internal static Dictionary<(string Room, string Sender), string> Load(SqliteConnection db, IEnumerable<string> rooms)
    {
        var result = new Dictionary<(string, string), string>();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA table_info(chat_room)";
        var columns = new HashSet<string>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("username") || !columns.Contains("ext_buffer")) return result;
        command.CommandText = "SELECT ext_buffer FROM chat_room WHERE username=$room";
        var parameter = command.Parameters.Add("$room", SqliteType.Text);
        foreach (string room in rooms.Distinct(StringComparer.Ordinal))
        {
            parameter.Value = room;
            if (command.ExecuteScalar() is not byte[] bytes || !TryParse(bytes, out var members)) continue;
            foreach (var member in members) result[(room, member.Key)] = member.Value;
        }
        return result;
    }

    internal static bool TryParse(byte[] bytes, out Dictionary<string, string> members)
    {
        members = new(StringComparer.Ordinal);
        if (bytes.Length > 16 * 1024 * 1024) return false;
        try
        {
            foreach (var field in Fields(bytes))
            {
                if (field.Number != 1 || field.Value == null) continue;
                string? sender = null, nickname = null;
                foreach (var member in Fields(field.Value))
                {
                    if (member.Value == null) continue;
                    if (member.Number == 1) sender = new UTF8Encoding(false, true).GetString(member.Value);
                    if (member.Number == 2) nickname = new UTF8Encoding(false, true).GetString(member.Value);
                }
                if (!string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(nickname)) members[sender] = nickname;
            }
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or OverflowException)
        {
            members.Clear(); return false;
        }
    }

    private static IEnumerable<(int Number, byte[]? Value)> Fields(byte[] bytes)
    {
        int offset = 0;
        ulong Varint()
        {
            ulong value = 0;
            for (int bits = 0; bits < 70 && offset < bytes.Length; bits += 7)
            {
                byte b = bytes[offset++];
                if (bits == 63 && b > 1) throw new InvalidDataException();
                value |= (ulong)(b & 127) << bits;
                if (b < 128) return value;
            }
            throw new InvalidDataException();
        }
        while (offset < bytes.Length)
        {
            ulong tag = Varint();
            int number = checked((int)(tag >> 3));
            if (number == 0) throw new InvalidDataException();
            byte[]? value = null;
            switch (tag & 7)
            {
                case 0: Varint(); break;
                case 1: offset = checked(offset + 8); break;
                case 5: offset = checked(offset + 4); break;
                case 2:
                    int length = checked((int)Varint());
                    if (length > bytes.Length - offset) throw new InvalidDataException();
                    value = bytes.AsSpan(offset, length).ToArray(); offset += length; break;
                default: throw new InvalidDataException();
            }
            if (offset > bytes.Length) throw new InvalidDataException();
            yield return (number, value);
        }
    }
}
