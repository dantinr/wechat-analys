namespace WeChatNotes;

internal static class ExportProgressSelfTest
{
    private sealed class Observer(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress progress) => report(progress);
    }
    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using var store = new Store(Path.Combine(root, "export-progress-store"));
        string source = Path.Combine(root, "export-progress-account", "db_storage"); Directory.CreateDirectory(source);
        store.SaveTarget(new("progress", "合成导出群", "", 0, 0, 0, 0, 0, 0, source, "synthetic@chatroom"));
        CaptureRecord Record(string id, string text, long type = 1) => new(id, "progress", "合成导出群", DateTimeOffset.Now, text, text, "", "合成数据",
            new("message_0.db", 1, 1, "sender", "合成发送人", DateTimeOffset.Now, type, text));
        store.AddMessages(Enumerable.Range(0, 501).Select(i => Record("text-" + i, "合成正文-不应出现在进度通知中")));
        store.AddMessages([Record("file", "<msg><appmsg><title>未下载.txt</title><type>6</type><appattach><totallen>7</totallen></appattach></appmsg></msg>", 49), Record("voice", "<msg><voicemsg/></msg>", 34)]);
        string folder = Path.Combine(root, "export-progress-output");
        var updates = new List<ExportProgress>(); bool filesReadyAtCompletion = false;
        var progress = new Observer(p =>
        {
            updates.Add(p);
            if (p.Stage == "导出完成") filesReadyAtCompletion = File.ReadAllLines(Path.Combine(folder, "聊天记录.jsonl")).Length == 503 && File.ReadAllText(Path.Combine(folder, "使用说明.txt")).Length > 0;
        });
        Exporter.Export(store, folder, "progress", (_, selected) => selected.ToDictionary(c => c.Id, _ => new VoicePayload([1, 2, 3], "合成音频")), progress);
        check("导出按真实步骤报告附件语音和文档进度", new[] { "整理消息内容", "导出文件附件", "读取本地语音库", "导出语音", "写入聊天记录", "生成对话文档" }.All(stage => updates.Any(p => p.Stage == stage)));
        check("进度数量有效且完成通知发生在文件写出之后", filesReadyAtCompletion && updates.Last() is { Stage: "导出完成", Percent: 100 } && updates.All(p => p.Completed >= 0 && (p.Total == 0 || p.Completed <= p.Total)) && updates.Where(p => p.Total > 0).GroupBy(p => p.Stage).All(g => g.Last().Percent == 100) && updates.All(p => !p.Stage.Contains("合成正文")));

        string failed = Path.Combine(root, "export-progress-failed"); Directory.CreateDirectory(Path.Combine(failed, "使用说明.txt"));
        var failedUpdates = new List<ExportProgress>(); bool rejected = false;
        try { Exporter.Export(store, failed, "progress", (_, selected) => selected.ToDictionary(c => c.Id, _ => new VoicePayload(null, "未加载")), new Observer(failedUpdates.Add)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rejected = true; }
        check("最后一步写入失败不会报告导出完成", rejected && failedUpdates.All(p => p.Stage != "导出完成"));
        var missingUpdates = new List<ExportProgress>();
        Exporter.Export(store, Path.Combine(root, "export-progress-missing-voice"), "progress", (_, _) => throw new InvalidOperationException("合成语音不可用"), new Observer(missingUpdates.Add));
        check("语音不可用时进度继续并完成文字导出", missingUpdates.Any(p => p.Stage == "导出语音" && p.Percent == 100) && missingUpdates.Last().Stage == "导出完成");
        var emptyUpdates = new List<ExportProgress>();
        Exporter.Export(store, Path.Combine(root, "export-progress-empty"), "empty", new Observer(emptyUpdates.Add));
        check("空会话导出进度不会除零或卡住", emptyUpdates.Last() is { Stage: "导出完成", Percent: 100 });

        using (var rendered = new ManualResetEventSlim())
        using (var preview = new ExportProgressDialog(_ => rendered.Wait(TimeSpan.FromSeconds(10))) { Opacity = 0 })
        {
            preview.Shown += async (_, _) =>
            {
                try
                {
                    preview.SetProgress(new("写入聊天记录", 250, 1000));
                    await Task.Delay(500); preview.PerformLayout();
                    var bar = preview.Controls[0].Controls.OfType<ProgressBar>().Single();
                    bool exact = bar.Style == ProgressBarStyle.Continuous && bar.Value == 25;
                    using var bitmap = new Bitmap(preview.Width, preview.Height); preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size));
                    bitmap.Save(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-export-progress.png"));
                    preview.SetProgress(new("读取本地语音库"));
                    check("已知数量显示百分比未知数量切换滚动进度", exact && bar.Style == ProgressBarStyle.Marquee);
                }
                finally { rendered.Set(); }
            };
            preview.ShowDialog();
        }
        int uiThread = Environment.CurrentManagedThreadId; bool background = false;
        using (var dialog = new ExportProgressDialog(p => { background = Environment.CurrentManagedThreadId != uiThread; p.Report(new("导出完成", 1, 1)); }) { Opacity = 0 })
            check("进度窗口在后台执行任务并在成功后关闭", dialog.ShowDialog() == DialogResult.OK && background && dialog.Failure == null);
        using (var dialog = new ExportProgressDialog(_ => throw new IOException("合成导出失败")) { Opacity = 0 })
            check("导出异常会关闭进度窗口并返回失败", dialog.ShowDialog() == DialogResult.Abort && dialog.Failure is IOException);
    }
}
