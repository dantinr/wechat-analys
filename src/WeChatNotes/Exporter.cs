using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WeChatNotes;

public static class Exporter
{
    public static void Export(Store store, string folder)
    {
        Directory.CreateDirectory(folder);
        var captures = store.Captures();
        var requirements = store.Requirements();
        var utf8 = new UTF8Encoding(true);
        using (var json = new StreamWriter(Path.Combine(folder, "聊天记录.jsonl"), false, new UTF8Encoding(false)))
        using (var csv = new StreamWriter(Path.Combine(folder, "聊天记录.csv"), false, utf8))
        using (var markdown = new StreamWriter(Path.Combine(folder, "聊天记录.md"), false, utf8))
        {
            csv.WriteLine("记录ID,会话,发送人,发送时间,消息类型,消息原文,数据库原始正文,消息编号,本地编号,来源数据库,记录时间,旧版截图,说明");
            markdown.WriteLine("# 微信聊天记录\n\n消息直接读取自本地数据库，时间为消息发送时间（UTC+08:00）。只包括选定会话及导入范围内、已同步到这台电脑的消息。图片、语音和附件只保留数据库中的文字说明或 XML。旧版 OCR 记录会单独注明。\n");
            foreach (var capture in captures)
            {
                // Only generated GUID paths are copied. Exporting a database must never read arbitrary paths.
                string imagePath = "";
                if (!string.IsNullOrEmpty(capture.ImagePath))
                {
                    string relative = Path.Combine("screenshots", capture.Id + ".png");
                    string source = store.ImageFile(capture.ImagePath);
                    string destination = Path.Combine(folder, relative);
                    if (File.Exists(source))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        File.Copy(source, destination, true); imagePath = relative.Replace('\\', '/');
                    }
                }
                var message = capture.Message;
                json.WriteLine(JsonSerializer.Serialize(capture with { ImagePath = imagePath }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                csv.WriteLine(string.Join(',', new[] { capture.Id, capture.TargetName, message?.Sender ?? "旧版 OCR", message?.SentAt.ToString("O") ?? "", message == null ? "旧版 OCR" : RawChatSource.TypeName(message.RawType), capture.Text, message?.RawContent ?? capture.Text, message?.ServerId.ToString() ?? "", message?.LocalId.ToString() ?? "", message?.SourceDatabase ?? "", capture.ObservedAt.ToString("O"), imagePath, capture.Note }.Select(Csv)));
                markdown.WriteLine($"<a id=\"record-{capture.Id}\"></a>\n\n## {Md(capture.TargetName)} · {(message?.SentAt ?? capture.ObservedAt):yyyy-MM-dd HH:mm:ss zzz}\n\n发送人：{Md(message?.Sender ?? "旧版 OCR：发送人与时间需核对截图")}\n\n记录 ID：`{capture.Id}`\n\n{Quote(capture.Text)}\n\n{Md(capture.Note)}\n");
                if (message != null) markdown.WriteLine($"消息编号：{message.ServerId}；本地编号：{message.LocalId}；来源：{Md(message.SourceDatabase)}\n");
                if (imagePath.Length > 0) markdown.WriteLine($"[查看旧版截图]({imagePath})\n");
            }
        }
        using (var file = new StreamWriter(Path.Combine(folder, "需求整理.md"), false, utf8))
        {
            file.WriteLine("# 需求整理\n\n以下条目由使用者手动整理，来源链接指向原始聊天消息。\n");
            if (requirements.Count == 0) file.WriteLine("暂无需求。请在程序中选中采集记录，点击“整理为需求”。\n");
            foreach (var item in requirements)
                file.WriteLine($"## {Md(item.Title)}\n\n状态：{Md(item.Status)}\n\n{Quote(item.Notes)}\n\n[查看来源消息](聊天记录.md#record-{item.CaptureId})\n");
        }
        File.WriteAllText(Path.Combine(folder, "使用说明.txt"), "本目录为微信需求记录的本地导出。\n聊天记录.md：消息原文、发送人、发送时间和来源编号。\n聊天记录.csv：适合表格软件。\n聊天记录.jsonl：每行一条完整记录，Message.RawContent 保留数据库原始正文。\n需求整理.md：手动整理的需求及来源链接。\n只包含本机已同步、选定会话及导入范围内的消息。图片、语音和附件不提取原文件。旧版 OCR 记录有明确标记。\n", utf8);
    }

    internal static string Csv(string text)
    {
        // A chat message is untrusted spreadsheet input, including after leading whitespace.
        string trimmed = text.TrimStart();
        if (trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || text[0] is '\t' or '\r' or '\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    private static string Md(string text) => System.Net.WebUtility.HtmlEncode(text).Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("#", "\\#");
    private static string Quote(string text) => string.Join('\n', Md(text).Replace("\r", "").Split('\n').Select(line => "> " + line));
}
