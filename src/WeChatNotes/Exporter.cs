using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WeChatNotes;

public static class Exporter
{
    public static void Export(Store store, string folder, string? targetId = null, IProgress<ExportProgress>? progress = null, VoiceTranscriptionOptions? transcription = null)
    {
        using var engine = transcription == null ? null : new OfflineTranscriber(transcription.ModelPath);
        Export(store, folder, targetId, null, progress, engine == null ? null : engine.Transcribe);
    }

    internal static void Export(Store store, string folder, string? targetId,
        Func<string, IReadOnlyList<CaptureRecord>, Dictionary<string, VoicePayload>>? readVoices, IProgress<ExportProgress>? progress = null,
        Func<string, VoiceTranscript>? transcribe = null)
    {
        var updates = new ExportProgressReporter(progress);
        updates.Report(new("准备导出记录"));
        Directory.CreateDirectory(folder);
        var captures = store.Captures(targetId: targetId);
        var content = new Dictionary<string, ReadableMessage>(StringComparer.Ordinal);
        int completed = 0;
        updates.Report(new("整理消息内容", 0, captures.Count));
        foreach (var capture in captures)
        {
            content[capture.Id] = MessageContent.Read(capture);
            updates.Report(new("整理消息内容", ++completed, captures.Count));
        }
        var copier = new AttachmentExporter(store, folder);
        var attachments = new Dictionary<string, ExportedAttachment>(StringComparer.Ordinal);
        var files = captures.Where(c => content[c.Id].File != null).ToList();
        completed = 0;
        if (files.Count > 0) updates.Report(new("导出文件附件", 0, files.Count));
        foreach (var capture in files)
        {
            attachments[capture.Id] = copier.Copy(capture, content[capture.Id].File!);
            updates.Report(new("导出文件附件", ++completed, files.Count));
        }
        var voices = new VoiceExporter(store, folder, readVoices).Copy(captures, updates);
        var transcripts = new Dictionary<string, VoiceTranscript>(StringComparer.Ordinal);
        if (transcribe != null)
        {
            completed = 0; updates.Report(new("离线语音转文字", 0, voices.Count));
            foreach (var (id, voice) in voices)
            {
                if (voice.RelativePath is not { } path) transcripts[id] = new(null, "本地音频未导出，无法转写");
                else if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)) transcripts[id] = new(null, "当前音频未转换为 WAV，请收听原音");
                else
                {
                    try { transcripts[id] = transcribe(Path.Combine(folder, path)); }
                    catch (Exception) { transcripts[id] = new(null, "语音识别失败，请收听原音后重试"); }
                }
                updates.Report(new("离线语音转文字", ++completed, voices.Count));
            }
        }
        var utf8 = new UTF8Encoding(true);
        completed = 0; updates.Report(new("写入聊天记录", 0, captures.Count));
        using (var json = new StreamWriter(Path.Combine(folder, "聊天记录.jsonl"), false, new UTF8Encoding(false)))
        using (var csv = new StreamWriter(Path.Combine(folder, "聊天记录.csv"), false, utf8))
        using (var markdown = new StreamWriter(Path.Combine(folder, "聊天记录.md"), false, utf8))
        {
            csv.WriteLine("记录ID,会话,发送人,群昵称,发送时间,消息类型,消息原文,数据库原始正文,消息编号,本地编号,来源数据库,记录时间,旧版截图,说明,语音文件,语音导出状态,原始语音文件,语音转写,语音转写状态,语音识别模型");
            markdown.WriteLine("# 微信聊天记录\n\n消息直接读取自本地数据库，时间为消息发送时间（UTC+08:00）。只包括选定会话及导入范围内、已同步到这台电脑的消息。可匹配的本地文件附件和语音一并导出；SILK 语音自动转换为可播放的 WAV，同时保留原始 SILK。启用离线识别时，语音转写单独标注，不替换消息原文；识别内容请结合原音核对。图片、视频等保留数据库中的文字说明或 XML。旧版 OCR 记录会单独注明。\n");
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
                voices.TryGetValue(capture.Id, out var voice);
                transcripts.TryGetValue(capture.Id, out var transcript);
                json.WriteLine(JsonSerializer.Serialize(capture with { ImagePath = imagePath, VoiceTranscription = transcript }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                csv.WriteLine(string.Join(',', new[] { capture.Id, capture.TargetName, message?.Sender ?? "旧版 OCR", message?.GroupNickname ?? "", message?.SentAt.ToString("O") ?? "", message == null ? "旧版 OCR" : RawChatSource.TypeName(message.RawType), capture.Text, message?.RawContent ?? capture.Text, message?.ServerId.ToString() ?? "", message?.LocalId.ToString() ?? "", message?.SourceDatabase ?? "", capture.ObservedAt.ToString("O"), imagePath, capture.Note, voice?.RelativePath ?? "", voice?.Status ?? "", voice?.OriginalPath ?? "", transcript?.Text ?? "", transcript?.Status ?? "", transcript?.Model ?? "" }.Select(Csv)));
                markdown.WriteLine($"<a id=\"record-{capture.Id}\"></a>\n\n## {Md(capture.TargetName)} · {(message?.SentAt ?? capture.ObservedAt):yyyy-MM-dd HH:mm:ss zzz}\n\n发送人：{Md(message?.Sender ?? "旧版 OCR：发送人与时间需核对截图")}\n\n记录 ID：`{capture.Id}`\n\n{Quote(capture.Text)}\n\n{Md(capture.Note)}\n");
                if (message != null) markdown.WriteLine($"消息编号：{message.ServerId}；本地编号：{message.LocalId}；来源：{Md(message.SourceDatabase)}\n");
                if (message?.GroupNickname is { Length: > 0 } nickname) markdown.WriteLine($"群昵称：{Md(nickname)}\n");
                if (attachments.TryGetValue(capture.Id, out var attachment)) markdown.WriteLine(AttachmentText(attachment) + "\n");
                if (voice != null) markdown.WriteLine(TranscriptText(transcript) + VoiceText(voice) + "\n");
                if (imagePath.Length > 0) markdown.WriteLine($"[查看旧版截图]({imagePath})\n");
                updates.Report(new("写入聊天记录", ++completed, captures.Count));
            }
        }
        completed = 0; updates.Report(new("生成对话文档", 0, captures.Count));
        using (var dialogue = new StreamWriter(Path.Combine(folder, "对话内容.md"), false, utf8))
        {
            dialogue.WriteLine("# 对话内容\n");
            bool multiple = captures.Select(c => c.TargetId).Distinct(StringComparer.Ordinal).Skip(1).Any();
            foreach (var capture in captures.OrderBy(c => c.Message?.SentAt ?? c.ObservedAt))
            {
                string sender = capture.Message?.DisplaySender ?? "旧版 OCR（发送人待核对）";
                string prefix = multiple ? Md(capture.TargetName) + " · " : "";
                string body = attachments.TryGetValue(capture.Id, out var attachment) ? AttachmentText(attachment)
                    : voices.TryGetValue(capture.Id, out var voice) ? TranscriptText(transcripts.GetValueOrDefault(capture.Id)) + VoiceText(voice) : Md(content[capture.Id].Text);
                var time = (capture.Message?.SentAt ?? capture.ObservedAt).ToOffset(TimeSpan.FromHours(8));
                dialogue.WriteLine($"{prefix}{Md(sender)}：{body} （{(capture.Message == null ? "记录于 " : "")}{time:yyyy-MM-dd HH:mm:ss zzz}）\n".ReplaceLineEndings(Environment.NewLine));
                updates.Report(new("生成对话文档", ++completed, captures.Count));
            }
        }
        File.WriteAllText(Path.Combine(folder, "使用说明.txt"), "本目录为Ripper的本地导出。\n对话内容.md：按发送时间排序的“发送人：对话内容 + 时间”，文件和语音消息附带本地链接或未导出说明。\n聊天记录.md：消息原文、发送人、发送时间和来源编号。\n聊天记录.csv：适合表格软件，包含语音文件相对路径、导出状态和原始语音路径。\n聊天记录.jsonl：每行一条完整记录，Message.RawContent 保留数据库原始正文。\n附件/：已下载并匹配的文件附件，保留原始文件名；不同消息分目录保存，避免同名文件覆盖。\n语音/：按会话、消息编号及发送时间匹配的本地原始音频，不同消息分目录保存；SILK 自动转换为 24 kHz、单声道、16 位 PCM WAV，可用常见播放器打开，同时保留 .silk 原文件；转换失败时保留原文件并注明原因。启用离线语音识别后，识别文字以“语音转写”标注并与原音链接一并导出；CSV 增加转写文字、状态和模型，JSONL 的 VoiceTranscription 保存识别结果，原文保持不变。自动识别可能有误，请结合原音核对。未识别的音频格式按 .bin 原样保存。\n只包含本机已同步、选定会话及导入范围内的消息。未找到的文件或语音在对话内容中明确标注；请保持微信登录，先在微信下载或加载后重新导出。图片和视频不提取原文件。旧版 OCR 记录有明确标记。\n", utf8);
        updates.Report(new("导出完成", 1, 1));
    }

    internal static string Csv(string text)
    {
        // A chat message is untrusted spreadsheet input, including after leading whitespace.
        string trimmed = text.TrimStart();
        if (trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || text[0] is '\t' or '\r' or '\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    private static string AttachmentText(ExportedAttachment file) => file.RelativePath is { } path
        ? $"文件 [{Md(file.Name)}]({string.Join('/', path.Split('/').Select(Uri.EscapeDataString))})"
        : $"文件：{Md(file.Name.Length == 0 ? "未提供文件名" : file.Name)}（附件未导出：{Md(file.Status)}）";
    private static string TranscriptText(VoiceTranscript? transcript) => transcript == null ? ""
        : transcript.Text is { Length: > 0 } text ? $"{Md(text)}〔语音转写〕  \n"
        : $"〔语音未转写：{Md(transcript.Status)}〕  \n";
    private static string VoiceText(ExportedVoice voice) => voice.RelativePath is { } path
        ? $"语音 [{Md(voice.Name)}]({string.Join('/', path.Split('/').Select(Uri.EscapeDataString))})" + (voice.OriginalPath is { } original
            ? $"（[原始 SILK]({string.Join('/', original.Split('/').Select(Uri.EscapeDataString))})）" : $"（{Md(voice.Status)}）")
        : $"[语音]（语音未导出：{Md(voice.Status)}）";
    private static string Md(string text) => text.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`").Replace("#", "\\#").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").ReplaceLineEndings("  " + Environment.NewLine);
    private static string Quote(string text) => string.Join('\n', Md(text).Replace("\r", "").Split('\n').Select(line => "> " + line));
}
