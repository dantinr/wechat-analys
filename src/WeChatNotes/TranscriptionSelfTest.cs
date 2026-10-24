using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace WeChatNotes;

internal static class TranscriptionSelfTest
{
    private sealed class Observer(Action<ExportProgress> action) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => action(value); }

    internal static void RunModel(string root, string modelPath, string syntheticWave, string report, Action<string, bool> check)
    {
        // Explicit opt-in only: normal synthetic tests never download a model or use personal audio.
        using var store = new Store(Path.Combine(root, "speech-model-integration"));
        store.SaveTarget(new("speech", "合成识别群", "", 0, 0, 0, 0, 0, 0, Path.Combine(root, "synthetic-speech-account", "db_storage"), "speech@chatroom"));
        var time = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(8));
        store.AddMessages([new("speech", "speech", "合成识别群", time, "<msg><voicemsg/></msg>", "", "", "合成TTS音频",
            new("message_0.db", 1, 1, "synthetic", "合成发送人", time, 34, "合成语音原文"))]);
        SpeechModel.SavePath(store.Root, modelPath);
        using var engine = new OfflineTranscriber(SpeechModel.PathFor(store.Root));
        string folder = Path.Combine(root, "speech-model-export");
        Exporter.Export(store, folder, "speech", (_, _) => new() { ["speech"] = new(File.ReadAllBytes(syntheticWave), "合成TTS") }, null, engine.Transcribe);
        var exported = JsonSerializer.Deserialize<CaptureRecord>(File.ReadAllText(Path.Combine(folder, "聊天记录.jsonl")))!;
        check("真实离线模型识别合成语音并通过发布版本导出为聊天文字", exported.VoiceTranscription?.Text is { Length: > 2 } && exported.Message!.RawContent == "合成语音原文" && File.ReadAllText(Path.Combine(folder, "对话内容.md")).Contains("〔语音转写〕"));
        using var dialog = new SpeechModelDialog(store.Root) { Opacity = 0, ShowInTaskbar = false };
        var save = Descendants(dialog).OfType<Button>().Single(b => b.Text == "保存并使用");
        using var timeout = new System.Windows.Forms.Timer { Interval = 100 };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        timeout.Tick += (_, _) => { if (save.Enabled || DateTime.UtcNow >= deadline) { timeout.Stop(); dialog.Close(); } };
        dialog.Shown += (_, _) =>
        {
            dialog.PerformLayout();
            using var preview = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(preview, new(Point.Empty, dialog.Size));
            preview.Save(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-speech-model.png"));
            save.PerformClick(); timeout.Start();
        };
        dialog.ShowDialog(); timeout.Stop();
        if (dialog.Failure != null) throw new InvalidOperationException("保存并使用真实模型失败", dialog.Failure);
        check("发布版通过保存并使用按钮完成真实模型校验及路径保存", dialog.DialogResult == DialogResult.OK && SpeechModel.PathFor(store.Root) == Path.GetFullPath(modelPath));
    }

    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));

    internal static void Run(string root, Action<string, bool> check)
    {
        string data = Path.Combine(root, "speech-tests"); Directory.CreateDirectory(data);
        string model = Path.Combine(data, "model.bin"); byte[] bytes = [1, 2, 3, 4];
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        var updates = new List<ExportProgress>(); var progress = new Observer(updates.Add);
        void Install(byte[] source, string digest, CancellationToken token = default) => Task.Run(async () =>
        {
            using var stream = new MemoryStream(source);
            await SpeechModel.InstallAsync(stream, model, bytes.Length, digest, progress, token);
        }).GetAwaiter().GetResult();
        Install(bytes, hash);
        check("模型下载完成后校验哈希并报告真实字节进度", File.ReadAllBytes(model).SequenceEqual(bytes) && updates.Any(p => p.Completed == 4 && p.Total == 4));
        bool Reject(byte[] source, string digest, CancellationToken token = default)
        {
            try { Install(source, digest, token); return false; }
            catch (Exception ex) when (ex is InvalidDataException or OperationCanceledException) { return File.ReadAllBytes(model).SequenceEqual(bytes) && Directory.GetFiles(data, "*.partial").Length == 0; }
        }
        check("错误模型不能替换已校验模型且清理临时文件", Reject([4, 3, 2, 1], hash));
        check("未下载完整的模型不能安装", Reject([1, 2], hash));
        check("超大模型不能安装或覆盖原文件", Reject([1, 2, 3, 4, 5], hash));
        using (var cancellation = new CancellationTokenSource())
        { cancellation.Cancel(); check("取消下载保留原模型并清理临时文件", Reject(bytes, hash, cancellation.Token)); }

        string settings = Path.Combine(data, "model-settings");
        check("未配置模型路径时兼容已有默认下载目录", SpeechModel.PathFor(settings) == SpeechModel.DefaultPathFor(settings));
        string external = Path.Combine(data, "用户预下载 中文目录", "my-whisper-small.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!); File.WriteAllBytes(external, bytes);
        Task.Run(() => SpeechModel.ValidateAsync(external, CancellationToken.None, bytes.Length, hash)).GetAwaiter().GetResult();
        check("预下载模型只读校验且不改变源文件", File.ReadAllBytes(external).SequenceEqual(bytes));
        SpeechModel.SavePath(settings, external);
        check("外部中文空格路径持久保存且不复制模型", SpeechModel.PathFor(settings) == external && File.Exists(Path.Combine(settings, "speech-model.json")) && !File.Exists(SpeechModel.DefaultPathFor(settings)));
        bool invalidModel = false;
        try { Task.Run(() => SpeechModel.ValidateAsync(external, CancellationToken.None, bytes.Length, new string('0', 64))).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { invalidModel = true; }
        check("外部模型校验失败时保留已选择路径", invalidModel && SpeechModel.PathFor(settings) == external);
        bool relative = false;
        try { SpeechModel.SavePath(settings, "relative.bin"); } catch (ArgumentException) { relative = true; }
        check("无效相对路径不会覆盖既有配置", relative && SpeechModel.PathFor(settings) == external && Directory.GetFiles(settings, "*.tmp").Length == 0);
        File.Delete(external);
        check("外部模型移走后保留用户选择并明确判定不可用", SpeechModel.PathFor(settings) == external && !SpeechModel.Exists(settings));
        File.WriteAllText(Path.Combine(settings, "speech-model.json"), "invalid-json");
        check("模型路径配置损坏时回退默认目录且不阻止启动", SpeechModel.PathFor(settings) == SpeechModel.DefaultPathFor(settings));
        using (var modelDialog = new SpeechModelDialog(settings) { Opacity = 0, ShowInTaskbar = false })
        using (var closeDialog = new System.Windows.Forms.Timer { Interval = 100 })
        {
            Descendants(modelDialog).OfType<TextBox>().Single().Text = Path.Combine(data, "not-found.bin");
            modelDialog.Shown += (_, _) => { Descendants(modelDialog).OfType<Button>().Single(b => b.Text == "保存并使用").PerformClick(); closeDialog.Start(); };
            closeDialog.Tick += (_, _) => { closeDialog.Stop(); modelDialog.Close(); };
            modelDialog.ShowDialog();
            check("保存模型失败显示具体原因并停止校验进度动画", modelDialog.Failure is FileNotFoundException && Descendants(modelDialog).OfType<Label>().Any(l => l.Text.Contains("找不到模型文件")) && Descendants(modelDialog).OfType<ProgressBar>().Single().Style == ProgressBarStyle.Blocks);
        }

        byte[] Wave(short value, int rate = 24000, int channels = 1, bool opposite = false)
        {
            int frames = rate / 5; byte[] wave = new byte[44 + frames * channels * 2];
            "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), (short)channels);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), rate * channels * 2);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), (short)(channels * 2)); BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
            "data"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), wave.Length - 44);
            for (int i = 0; i < frames * channels; i++) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), opposite && i % channels == 1 ? (short)-value : value);
            return wave;
        }
        string wav = Path.Combine(data, "input.wav"); File.WriteAllBytes(wav, Wave(16384));
        var samples = OfflineTranscriber.ReadSamples(wav);
        check("24kHz微信WAV正确重采样为16kHz单声道", samples.Length == 3200 && samples.All(s => Math.Abs(s - .5f) < .0001));
        File.WriteAllBytes(wav, Wave(16384, 48000, 2, true));
        check("立体声输入正确合并且静音不会伪造文字", !OfflineTranscriber.HasSpeechEnergy(OfflineTranscriber.ReadSamples(wav)));
        using (var engine = new OfflineTranscriber(Path.Combine(data, "missing-model.bin")))
            check("静音输入无需加载模型且明确注明未识别", engine.Transcribe(wav) is { Text: null, Status: "未检测到可识别语音" });
        File.WriteAllBytes(wav, [1, 2, 3]); bool bad = false;
        try { OfflineTranscriber.ReadSamples(wav); } catch (InvalidDataException) { bad = true; }
        check("损坏音频不作为有效采样送入识别引擎", bad);

        using var store = new Store(Path.Combine(data, "store"));
        store.SaveTarget(new("chat", "合成语音群", "", 0, 0, 0, 0, 0, 0, Path.Combine(data, "account", "db_storage"), "synthetic@chatroom"));
        var time = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(8));
        CaptureRecord Record(string id, long type, int offset) => new(id, "chat", "合成语音群", time, type == 34 ? "<msg><voicemsg/></msg>" : "合成文字", "", "", "合成测试",
            new("message_0.db", offset, offset, "synthetic-sender", "合成发送人", time.AddSeconds(offset), type, "原始消息XML"));
        store.AddMessages([Record("success", 34, 1), Record("failure", 34, 2), Record("missing", 34, 3), Record("text", 1, 4)]);
        Dictionary<string, VoicePayload> Read(string _, IReadOnlyList<CaptureRecord> records) => records.ToDictionary(r => r.Id, r => r.Id == "missing" ? new VoicePayload(null, "未加载音频") : new VoicePayload(Wave(r.Id == "success" ? (short)16384 : (short)-16384), "合成WAV"));
        string text = "=合成转写\n<script>明天下午三点开会。</script>"; int calls = 0;
        VoiceTranscript Transcribe(string path)
        {
            calls++;
            if (OfflineTranscriber.ReadSamples(path)[0] < 0) throw new IOException("synthetic-private-diagnostic");
            return new(text, "已离线转写");
        }
        string folder = Path.Combine(data, "export"); updates.Clear();
        Exporter.Export(store, folder, "chat", Read, progress, Transcribe);
        string dialogue = File.ReadAllText(Path.Combine(folder, "对话内容.md"));
        check("转写作为聊天内容保留发送人时间和原音链接", dialogue.Contains("合成发送人：=合成转写") && dialogue.Contains("明天下午三点开会。") && dialogue.Contains("〔语音转写〕") && dialogue.Contains("2026-10-24 10:00:01") && dialogue.Contains("](" + Uri.EscapeDataString("语音") + "/"));
        var exported = File.ReadAllLines(Path.Combine(folder, "聊天记录.jsonl")).Select(line => JsonSerializer.Deserialize<CaptureRecord>(line)!).ToArray();
        string csv = File.ReadAllText(Path.Combine(folder, "聊天记录.csv"));
        check("CSV和JSONL保存转写结果并保留原始正文", csv.Contains("语音转写,语音转写状态,语音识别模型") && csv.Contains("\"'=合成转写") && exported.Single(r => r.Id == "success") is { VoiceTranscription.Text: var result, Message.RawContent: "原始消息XML" } && result == text);
        check("识别文字按Markdown转义并明确区分自动转写", dialogue.Contains("&lt;script&gt;") && !dialogue.Contains("<script>") && File.ReadAllText(Path.Combine(folder, "聊天记录.md")).Contains("〔语音转写〕"));
        check("单条识别失败或缺失音频不会中断其他聊天导出", calls == 2 && dialogue.Contains("语音识别失败") && dialogue.Contains("本地音频未导出") && dialogue.Contains("合成文字") && !dialogue.Contains("synthetic-private-diagnostic"));
        check("转写按语音条数报告进度并在文档完成后结束", updates.Any(p => p.Stage == "离线语音转文字" && p.Completed == 3 && p.Total == 3) && updates.Last().Stage == "导出完成");
        check("导出转写不修改已采集的数据库原文", store.Captures().All(c => c.VoiceTranscription == null && c.Message?.RawContent == "原始消息XML"));
    }
}
