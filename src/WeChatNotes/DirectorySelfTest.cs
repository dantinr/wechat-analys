using System.Text;

namespace WeChatNotes;

internal static class DirectorySelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        string fixture = Path.Combine(root, "directory-discovery");
        string documents = Path.Combine(fixture, "Documents");
        string MakeAccount(string parent, string name)
        {
            string db = Path.Combine(parent, name, "db_storage");
            foreach (string part in new[] { "session", "contact", "message" }) Directory.CreateDirectory(Path.Combine(db, part));
            File.WriteAllBytes(Path.Combine(db, "session", "session.db"), []);
            File.WriteAllBytes(Path.Combine(db, "contact", "contact.db"), []);
            File.WriteAllBytes(Path.Combine(db, "message", "message_0.db"), []);
            return db;
        }
        string standard = MakeAccount(Path.Combine(documents, "xwechat_files"), "synthetic-account");
        check("自动识别默认文档目录中的账号", WeChatDirectories.FindAccounts([documents]).SequenceEqual([standard]));
        string custom = Path.Combine(fixture, "自定义 聊天目录");
        string first = MakeAccount(Path.Combine(custom, "xwechat_files"), "account-one");
        string second = MakeAccount(Path.Combine(custom, "xwechat_files"), "account-two");
        string ini = Path.Combine(fixture, "location.ini");
        File.WriteAllText(ini, custom, new UTF8Encoding(false));
        string? configured = WeChatDirectories.ReadConfigurationRoot(ini, documents);
        check("配置中的中文自定义路径可发现多个账号", configured == custom && WeChatDirectories.FindAccounts([configured!]).ToHashSet().SetEquals([first, second]));
        check("数据根目录、账号目录和数据库目录去重", WeChatDirectories.FindAccounts([custom, Path.Combine(custom, "xwechat_files"), Path.GetDirectoryName(first)!, first, first.ToUpperInvariant()]).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([first, second]));
        string incomplete = Path.Combine(custom, "xwechat_files", "incomplete", "db_storage");
        Directory.CreateDirectory(incomplete);
        check("失效配置与不完整账号不会遮蔽有效目录", WeChatDirectories.FindAccounts([Path.Combine(fixture, "missing"), "relative-path", "bad\0path", custom]).ToHashSet().SetEquals([first, second]));
        File.WriteAllText(ini, "\"" + custom + "\"\r\n", Encoding.Unicode);
        check("带 BOM 的 UTF16 配置与引号换行可读取", WeChatDirectories.ReadConfigurationRoot(ini, documents) == custom);
        File.WriteAllText(ini, "MyDocument:", Encoding.UTF8);
        check("默认文档配置标记回退到系统文档目录", WeChatDirectories.ReadConfigurationRoot(ini, documents) == documents);
        File.WriteAllText(ini, "not-a-path", Encoding.UTF8);
        bool relativeRejected = WeChatDirectories.ReadConfigurationRoot(ini, documents) == null;
        File.WriteAllText(ini, new string('x', 16385), Encoding.UTF8);
        check("无效、缺失和超大配置被跳过", relativeRejected && WeChatDirectories.ReadConfigurationRoot(ini, documents) == null && WeChatDirectories.ReadConfigurationRoot(ini + ".missing", documents) == null);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        bool canceled = false;
        try { WeChatDirectories.FindAccounts([documents], cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        check("目录识别支持取消", canceled);
    }
}
