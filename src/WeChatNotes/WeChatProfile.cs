using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal sealed record WeChatProfile(string Name, byte[]? Avatar);

internal static class WeChatProfiles
{
    internal const int MaximumAvatarBytes = 4 * 1024 * 1024;

    internal static WeChatProfile? Read(string root, CancellationToken token)
    {
        string contact = Path.Combine(root, "contact", "contact.db");
        string head = Path.Combine(root, "head_image", "head_image.db");
        if (!File.Exists(contact) || !File.Exists(head)) return null;
        var keys = RawKeys.Find([contact, head], null, token);
        try
        {
            using var contacts = Open(contact);
            using var images = Open(head);
            return Read(contacts, images, Directory.GetParent(Path.TrimEndingDirectorySeparator(root))!.Name, token);
        }
        finally { foreach (byte[] key in keys.Values) CryptographicOperations.ZeroMemory(key); }

        SqliteConnection Open(string path)
        {
            byte[] bytes = CipherPages.Snapshot(path, keys[path], token);
            try { return MemoryDatabase.Open(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal static WeChatProfile? Read(SqliteConnection contacts, SqliteConnection images, string accountFolder, CancellationToken token)
    {
        // WeChat 4.x appends a short storage suffix to the account username.
        // Match only these exact identities; never substitute a recent contact's avatar.
        var candidates = new List<string> { accountFolder };
        var suffix = Regex.Match(accountFolder, @"^(.+)_([a-zA-Z0-9]{4,8})$", RegexOptions.CultureInvariant);
        if (suffix.Success) candidates.Add(suffix.Groups[1].Value);
        var matches = new List<(string Id, string Name)>();
        foreach (string id in candidates.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            using var command = contacts.CreateCommand();
            command.CommandText = "SELECT nick_name FROM contact WHERE username=$id LIMIT 1";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (reader.Read()) matches.Add((id, reader.IsDBNull(0) ? "" : reader.GetString(0)));
        }
        if (matches.Count != 1) return null;
        var account = matches[0];
        using var avatar = images.CreateCommand();
        avatar.CommandText = "SELECT image_buffer FROM head_image WHERE username=$id AND length(image_buffer)>0 AND length(image_buffer)<=$limit ORDER BY update_time DESC LIMIT 1";
        avatar.Parameters.AddWithValue("$id", account.Id);
        avatar.Parameters.AddWithValue("$limit", MaximumAvatarBytes);
        token.ThrowIfCancellationRequested();
        byte[]? bytes = avatar.ExecuteScalar() as byte[];
        return new(string.IsNullOrWhiteSpace(account.Name) ? "微信用户" : account.Name, bytes);
    }
}
