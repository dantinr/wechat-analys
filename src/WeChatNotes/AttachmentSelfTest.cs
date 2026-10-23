using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WeChatNotes;

internal static class AttachmentSelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        string account = Path.Combine(root, "file-account"), source = Path.Combine(account, "db_storage"), local = Path.Combine(account, "msg", "file", "2026-10");
        Directory.CreateDirectory(source); Directory.CreateDirectory(local);
        string data = Path.Combine(root, "attachment-store");
        using var store = new Store(data);
        store.SaveTarget(new("files", "合成文件群", "", 0, 0, 0, 0, 0, 0, source, "files@chatroom"));
        store.SaveTarget(new("other", "其他会话", "", 0, 0, 0, 0, 0, 0, source, "other@chatroom"));
        var time = new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.FromHours(8));
        CaptureRecord Record(string id, string sender, string text, int minute, long type = 1, string target = "files") =>
            new(id, target, target == "files" ? "合成文件群" : "其他会话", time, text, text, "", "合成测试",
                new("message_0.db", minute, minute, sender, sender, time.AddMinutes(minute), type, text));
        string Xml(string name, byte[] bytes, bool checksum = true, string extension = "txt") => new XElement("msg", new XElement("appmsg",
            new XElement("title", name), new XElement("type", 6), new XElement("appattach", new XElement("totallen", bytes.Length), new XElement("fileext", extension)),
            new XElement("md5", checksum ? Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant() : ""))).ToString(SaveOptions.DisableFormatting);
        byte[] original = Encoding.UTF8.GetBytes("需求附件内容🙂\r\n"), second = Encoding.UTF8.GetBytes("另一个同名文件的内容");
        string name = "需求 [v1] #1%.txt";
        File.WriteAllBytes(Path.Combine(local, name), original);
        File.WriteAllBytes(Path.Combine(local, "下载重命名.txt"), second);
        var file1 = Record("file1", "C", Xml(name, original), 3, 49);
        var file2 = Record("file2", "C", Xml(name, second), 4, 49);
        var missing = Record("missing", "B", Xml("未下载.txt", "missing"u8.ToArray()), 5, 49);
        string plain = "  原文 <需求> & emoji🙂\r\n第二行 *保留*";
        var first = Record("first", "A", plain, 1);
        var last = Record("last", "B", "收到，周五完成。", 2);
        var other = Record("other-file", "其他人", Xml("不应导出的文件.txt", original), 0, 49, "other");
        store.AddMessages([file2, missing, file1, last, first, other]);
        check("普通文字保留原始空白换行与中文表情", MessageContent.Read(first).Text == plain);
        var parsed = MessageContent.Read(file1);
        check("文件 XML 解析原始文件名大小与校验值", parsed.File?.Name == name && parsed.File.Length == original.Length && parsed.File.Md5 == Convert.ToHexString(MD5.HashData(original)).ToLowerInvariant());
        string folder = Path.Combine(root, "dialogue-export"); Exporter.Export(store, folder, "files");
        string dialogue = File.ReadAllText(Path.Combine(folder, "对话内容.md"));
        string unescaped = System.Net.WebUtility.HtmlDecode(dialogue).Replace("\\*", "*");
        check("纯对话 Markdown 按发送时间排列并包含发送人与时间", dialogue.IndexOf("A：", StringComparison.Ordinal) < dialogue.IndexOf("B：", StringComparison.Ordinal) && dialogue.Contains("2026-10-23 09:01:00 +08:00") && unescaped.Contains("原文 <需求> & emoji🙂") && !dialogue.Contains("<appmsg"));
        var links = Regex.Matches(dialogue, @"\]\(([^)]+)\)").Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).ToArray();
        check("附件链接可读取且文件名与对话中的原始文件名一致", links.Length == 2 && links.All(p => Path.GetFileName(p) == name) && links.All(p => File.Exists(Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar)))));
        var bytes = links.Select(p => File.ReadAllBytes(Path.Combine(folder, p.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
        check("同名不同内容分目录保存并准确匹配校验值", links.Distinct().Count() == 2 && bytes.Any(b => b.SequenceEqual(original)) && bytes.Any(b => b.SequenceEqual(second)));
        check("未下载文件保留文件名并明确注明未导出", dialogue.Contains("未下载.txt") && dialogue.Contains("附件未导出") && !Directory.GetFiles(folder, "未下载.txt", SearchOption.AllDirectories).Any());
        check("当前会话对话和附件不混入其他会话", !dialogue.Contains("其他人") && !dialogue.Contains("不应导出的文件") && Directory.GetFiles(Path.Combine(folder, "附件"), "*", SearchOption.AllDirectories).Length == 2);
        check("导出附件不改变微信原始文件", File.ReadAllBytes(Path.Combine(local, name)).SequenceEqual(original));

        var copier = new AttachmentExporter(store, Path.Combine(root, "file-edge-export"));
        var unsafeFile = MessageContent.Read(Record("escape", "A", Xml("..\\越界.txt", original), 1, 49)).File!;
        check("附件文件名不能包含路径或设备名", copier.Copy(first, unsafeFile).RelativePath == null && !new FileMessage("NUL.txt", 1, null).SafeName && !new FileMessage("C:\\secret.txt", 1, null).SafeName);
        string outside = Path.Combine(account, "outside.txt"); File.WriteAllBytes(outside, "outside-secret"u8.ToArray());
        var outsideRecord = Record("outside", "A", Xml("outside.txt", "outside-secret"u8.ToArray()), 1, 49);
        check("附件只在微信文件目录内匹配且不读取 XML 指定的任意路径", copier.Copy(outsideRecord, MessageContent.Read(outsideRecord).File!).RelativePath == null);
        var wrong = parsed.File! with { Md5 = new string('0', 32) };
        check("同名文件校验不符时不会误导出", copier.Copy(first, wrong).RelativePath == null);
        var noHash = Record("no-hash", "A", Xml(name, original, false), 1, 49);
        check("缺少校验值时仅接受大小一致且唯一的同名文件", copier.Copy(noHash, MessageContent.Read(noHash).File!).RelativePath != null);
        string duplicateFolder = Path.Combine(local, "duplicate"); Directory.CreateDirectory(duplicateFolder); File.WriteAllBytes(Path.Combine(duplicateFolder, name), original);
        var ambiguousCopier = new AttachmentExporter(store, Path.Combine(root, "ambiguous-export"));
        check("无校验值且有多个同名文件时明确保留未导出状态", ambiguousCopier.Copy(noHash, MessageContent.Read(noHash).File!).RelativePath == null);
        File.WriteAllBytes(Path.Combine(local, "未下载.txt"), "missing"u8.ToArray());
        string retry = Path.Combine(root, "download-retry-export"); Exporter.Export(store, retry, "files");
        check("在微信下载后重新导出即可补上附件无需重新采集", Directory.GetFiles(retry, "未下载.txt", SearchOption.AllDirectories).Length == 1);
        var quote = Record("quote", "A", "<msg><appmsg><title>可以这样做🙂</title><type>57</type><refermsg><displayname>B</displayname><content>需求说明</content></refermsg></appmsg></msg>", 1, 49);
        check("引用消息转为可读对话而不暴露 XML", MessageContent.Read(quote).Text == "可以这样做🙂\n引用 B：需求说明");
        var xxe = Record("xxe", "A", "<!DOCTYPE msg [<!ENTITY x SYSTEM 'file:///outside.txt'>]><msg><appmsg><title>&x;</title><type>6</type></appmsg></msg>", 1, 49);
        check("消息 XML 禁止 DTD 和外部实体", MessageContent.Read(xxe).File == null && !MessageContent.Read(xxe).Text.Contains("outside-secret"));
        check("媒体消息在纯对话中显示可读类型", MessageContent.Read(Record("image", "A", "<msg><img/></msg>", 1, 3)).Text == "[图片]");
        var zero = Record("zero", "A", Xml("空文件", [], extension: "txt"), 1, 49);
        File.WriteAllBytes(Path.Combine(local, "空文件.txt"), []);
        var zeroCopier = new AttachmentExporter(store, Path.Combine(root, "zero-export"));
        check("文件标题缺少扩展名时补全并支持零字节文件", MessageContent.Read(zero).File?.Name == "空文件.txt" && zeroCopier.Copy(zero, MessageContent.Read(zero).File!).RelativePath != null);
        store.RemoveTarget("files");
        using var reopened = new Store(data);
        var history = new AttachmentExporter(reopened, Path.Combine(root, "removed-history-export"));
        check("移除绑定和重启后仍保留附件来源用于历史导出", reopened.Targets().All(t => t.Id != "files") && history.Copy(file1, MessageContent.Read(file1).File!).RelativePath != null);
    }
}
