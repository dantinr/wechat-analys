using System.Security;
using System.Text;
using Microsoft.Win32;

namespace WeChatNotes;

internal static class WeChatDirectories
{
    internal static string[] Discover(IEnumerable<string>? knownRoots = null, CancellationToken token = default)
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new List<string>(knownRoots ?? []);
        foreach (string config in new[] { @"Tencent\xwechat\config", @"Tencent\WeChat\All Users\config", @"Tencent\Weixin\config" })
        {
            foreach (string file in Files(Path.Combine(roaming, config), "*.ini"))
            {
                token.ThrowIfCancellationRequested();
                if (ReadConfigurationRoot(file, documents) is { } root) roots.Add(root);
            }
        }
        foreach (string keyName in new[] { @"Software\Tencent\Weixin", @"Software\Tencent\WeChat" })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyName);
                foreach (string value in new[] { "FileSavePath", "SavePath" })
                    if (key?.GetValue(value) is string root) roots.Add(root);
            }
            catch (Exception ex) when (Unavailable(ex)) { }
        }
        roots.Add(documents);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents"));
        return FindAccounts(roots, token);
    }

    internal static string? ReadConfigurationRoot(string file, string documents)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 16 * 1024) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string value = reader.ReadToEnd().Trim().Trim('\0').Trim().Trim('"');
            if (value.Equals("MyDocument:", StringComparison.OrdinalIgnoreCase)) return Normalize(documents);
            return Normalize(value);
        }
        catch (Exception ex) when (Unavailable(ex)) { return null; }
    }

    internal static string[] FindAccounts(IEnumerable<string> roots, CancellationToken token = default)
    {
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string candidate)
        {
            token.ThrowIfCancellationRequested();
            if (IsAccount(candidate)) accounts.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)));
        }
        foreach (string root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (Normalize(root) is not { } path || !visited.Add(path)) continue;
            Add(path);
            Add(Path.Combine(path, "db_storage"));
            foreach (string container in new[] { path, Path.Combine(path, "xwechat_files") })
                foreach (string child in Directories(container)) Add(Path.Combine(child, "db_storage"));
        }
        return accounts.ToArray();
    }

    private static bool IsAccount(string path) =>
        File.Exists(Path.Combine(path, "session", "session.db")) &&
        File.Exists(Path.Combine(path, "contact", "contact.db")) &&
        Files(Path.Combine(path, "message"), "message_*.db").Length > 0;

    private static string? Normalize(string value)
    {
        try
        {
            value = Environment.ExpandEnvironmentVariables(value.Trim());
            return Path.IsPathFullyQualified(value) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) : null;
        }
        catch (Exception ex) when (Unavailable(ex)) { return null; }
    }
    private static string[] Directories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (Unavailable(ex)) { return []; }
    }
    private static string[] Files(string path, string pattern)
    {
        try { return Directory.GetFiles(path, pattern); }
        catch (Exception ex) when (Unavailable(ex)) { return []; }
    }
    private static bool Unavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;
}
