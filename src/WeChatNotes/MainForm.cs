using System.Diagnostics;

namespace WeChatNotes;

public sealed class MainForm : Form
{
    private readonly Store store;
    private readonly RawRecorder recorder;
    private readonly System.Windows.Forms.Timer timer = new();
    private CancellationTokenSource cancellation = new();
    private readonly DataGridView targets = Grid();
    private readonly DataGridView captures = Grid();
    private readonly DataGridView requirements = Grid();
    private readonly TextBox search = new() { Width = 260, PlaceholderText = "在最近 500 条消息中搜索" };
    private readonly TextBox detail = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(238, 241, 245) };
    private readonly Label badge = new() { AutoSize = true, ForeColor = Color.FromArgb(85, 99, 113), Text = "已暂停", Padding = new Padding(10) };
    private readonly Label footer = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(85, 99, 113) };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 60, Value = 3, Width = 55 };
    private readonly Button start;
    private readonly Button once;
    private List<CaptureRecord> recent = [];
    private bool exporting;
    private bool closing;
    private bool allowClose;

    public MainForm(Store store)
    {
        this.store = store; recorder = new(store);
        Text = "微信需求记录"; Width = 1380; Height = 940; MinimumSize = new(1060, 760);
        StartPosition = FormStartPosition.CenterScreen; Font = new("Microsoft YaHei UI", 10);
        BackColor = Color.FromArgb(246, 248, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 10), ColumnCount = 1, RowCount = 6 };
        foreach (var style in new RowStyle[] { new(SizeType.Absolute, 90), new(SizeType.Absolute, 46), new(SizeType.Absolute, 135), new(SizeType.Absolute, 48), new(SizeType.Percent, 100), new(SizeType.Absolute, 40) }) layout.RowStyles.Add(style);
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "微信需求记录", Location = new(0, 0), AutoSize = true, Font = new("Microsoft YaHei UI", 24, FontStyle.Bold), ForeColor = Color.FromArgb(24, 41, 57) });
        header.Controls.Add(new Label { Text = "将讨论留在本地，让每一条需求都有来源。", Location = new(3, 52), AutoSize = true, ForeColor = Color.FromArgb(95, 110, 125) });
        badge.Location = new(450, 12); header.Controls.Add(badge); layout.Controls.Add(header, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        toolbar.Controls.Add(Button("添加会话", AddTarget, true));
        toolbar.Controls.Add(Button("移除绑定", RemoveTarget));
        start = Button("开始记录", Toggle, true); toolbar.Controls.Add(start);
        once = Button("立即采集", async (_, _) => await Poll()); toolbar.Controls.Add(once);
        toolbar.Controls.Add(new Label { Text = "间隔（秒）", AutoSize = true, Padding = new Padding(8, 8, 0, 0) }); toolbar.Controls.Add(interval);
        toolbar.Controls.Add(Button("导出全部", Export)); toolbar.Controls.Add(Button("打开数据目录", (_, _) => OpenFolder(store.Root)));
        toolbar.Controls.Add(Button("使用说明", (_, _) => MessageBox.Show(this,
            "1. 保持 Windows 微信登录。\n2. 点击“添加会话”→“读取会话列表”，勾选群或联系人，可选择首次导入范围。\n3. 点击“开始记录”，默认每 3 秒同步一次。窗口可最小化，无需切换聊天。\n4. 选中原始消息，点击“整理为需求”，编辑需求名称、说明和状态。\n5. “导出全部”生成 Markdown、CSV、JSONL 和需求文档。\n\n通过只读连接本地聊天数据库获取原文，不使用 OCR。完整解密数据库只存在于内存中，只保存选定会话的消息。\n图片、语音和附件仅保留文字说明 / XML，不导出原文件。只读取已经同步到本机的消息。\n关闭程序、电脑休眠或微信退出期间停止同步；重启后点击开始可补采仍在本地的消息。\n微信升级可能需要适配。", "使用说明")));
        layout.Controls.Add(toolbar, 0, 1);
        targets.Columns.Add("name", "记录会话"); targets.Columns.Add("window", "记录起点"); targets.Columns.Add("status", "采集状态");
        targets.Columns[0].FillWeight = 25; targets.Columns[1].FillWeight = 25; targets.Columns[2].FillWeight = 70;
        layout.Controls.Add(targets, 0, 2);
        var filter = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        filter.Controls.Add(search); filter.Controls.Add(Button("整理为需求", MakeRequirement, true));
        filter.Controls.Add(new Label { Text = "原始消息文字 · 保留发送人、发送时间与消息编号", AutoSize = true, Padding = new Padding(8, 7, 0, 0), ForeColor = Color.DimGray });
        layout.Controls.Add(filter, 0, 3);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var recordsTab = new TabPage("采集记录"); var requirementsTab = new TabPage("需求清单（双击编辑）");
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1200, 500), SplitterDistance = 700, Panel1MinSize = 350, Panel2MinSize = 300 };
        captures.Columns.Add("time", "发送时间"); captures.Columns.Add("name", "会话"); captures.Columns.Add("text", "消息原文");
        captures.Columns[0].FillWeight = 28; captures.Columns[1].FillWeight = 25; captures.Columns[2].FillWeight = 65;
        split.Panel1.Controls.Add(captures);
        var previewTabs = new TabControl { Dock = DockStyle.Fill };
        var textTab = new TabPage("消息原文 / 来源"); textTab.Padding = new Padding(12); textTab.Controls.Add(detail);
        var imageTab = new TabPage("旧版截图"); imageTab.Controls.Add(picture); previewTabs.TabPages.AddRange([textTab, imageTab]);
        split.Panel2.Controls.Add(previewTabs); recordsTab.Controls.Add(split);
        requirements.Columns.Add("title", "需求"); requirements.Columns.Add("status", "状态"); requirements.Columns.Add("notes", "说明");
        requirements.Columns[0].FillWeight = 30; requirements.Columns[1].FillWeight = 15; requirements.Columns[2].FillWeight = 65;
        requirementsTab.Controls.Add(requirements); tabs.TabPages.AddRange([recordsTab, requirementsTab]); layout.Controls.Add(tabs, 0, 4);
        layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
        interval.ValueChanged += (_, _) => timer.Interval = (int)interval.Value * 1000;
        timer.Interval = 3000; timer.Tick += async (_, _) => await Poll();
        search.TextChanged += (_, _) => PopulateCaptures();
        captures.SelectionChanged += (_, _) => ShowCapture();
        requirements.CellDoubleClick += EditRequirement;
        recorder.Status += (id, message) =>
        {
            foreach (DataGridViewRow row in targets.Rows) if ((string?)row.Tag == id) row.Cells[2].Value = message;
        };
        recorder.Recorded += RefreshRecords;
        FormClosing += ClosingAsync;
        FormClosed += (_, _) => { timer.Dispose(); cancellation.Dispose(); recorder.Dispose(); picture.Image?.Dispose(); };
        Shown += (_, _) => split.SplitterDistance = Math.Clamp((int)(split.ClientSize.Width * .58), split.Panel1MinSize, split.ClientSize.Width - split.Panel2MinSize - split.SplitterWidth);
        RefreshTargets(); RefreshRecords();
    }

    private static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        AutoGenerateColumns = false, RowTemplate = { Height = 32 }, AllowUserToOrderColumns = false,
        ColumnHeadersHeight = 36, EnableHeadersVisualStyles = false,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(232, 238, 243), ForeColor = Color.FromArgb(45, 62, 79) },
        DefaultCellStyle = new DataGridViewCellStyle { SelectionBackColor = Color.FromArgb(219, 242, 233), SelectionForeColor = Color.FromArgb(20, 67, 51), ForeColor = Color.FromArgb(40, 52, 65), Padding = new Padding(5) }
    };
    private static Button Button(string text, EventHandler handler, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(17, 127, 91) : Color.White, ForeColor = primary ? Color.White : Color.FromArgb(45, 62, 79), Padding = new Padding(7, 0, 7, 0) };
        button.FlatAppearance.BorderColor = primary ? button.BackColor : Color.FromArgb(212, 220, 227); button.Click += handler; return button;
    }
    private void RefreshTargets()
    {
        targets.Rows.Clear(); foreach (var item in store.Targets())
        {
            int row = targets.Rows.Add(item.Name, item.SourceRoot == null ? "旧版画面绑定" : item.FromUnix == 0 ? "全部本地历史" : DateTimeOffset.FromUnixTimeSeconds(item.FromUnix).ToLocalTime().ToString("MM-dd HH:mm"), item.SourceRoot == null ? "请移除后重新添加会话" : "待开始 · 保持微信登录"); targets.Rows[row].Tag = item.Id;
        }
    }
    private void RefreshRecords()
    {
        recent = store.Captures(500); PopulateCaptures();
        requirements.Rows.Clear(); foreach (var item in store.Requirements())
        {
            int row = requirements.Rows.Add(item.Title, item.Status, item.Notes.ReplaceLineEndings(" ")); requirements.Rows[row].Tag = item;
        }
        footer.Text = $"最近 {recent.Count} 条消息 · {requirements.Rows.Count} 条需求 · 本机数据：{store.Root}";
    }
    private void PopulateCaptures()
    {
        string? selected = (captures.CurrentRow?.Tag as CaptureRecord)?.Id;
        captures.Rows.Clear();
        foreach (var item in recent.AsEnumerable().Reverse().Where(c => string.IsNullOrWhiteSpace(search.Text) || (c.Text + c.TargetName).Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
        {
            int index = captures.Rows.Add((item.Message?.SentAt ?? item.ObservedAt).ToString("MM-dd HH:mm:ss"), item.TargetName, item.AddedText.ReplaceLineEndings(" "));
            captures.Rows[index].Tag = item;
        }
        var row = captures.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as CaptureRecord)?.Id == selected) ?? captures.Rows.Cast<DataGridViewRow>().FirstOrDefault();
        if (row != null) { captures.CurrentCell = row.Cells[0]; row.Selected = true; }
        ShowCapture();
    }
    private void ShowCapture()
    {
        var item = captures.CurrentRow?.Tag as CaptureRecord;
        detail.Text = item == null ? "添加会话并开始记录后，这里显示原始消息。\r\n选择一条消息即可查看来源并整理需求。" : item.Message is { } message ?
            $"{item.TargetName}\r\n发送人：{message.Sender}\r\n发送于 {message.SentAt:yyyy-MM-dd HH:mm:ss zzz}\r\n记录于 {item.ObservedAt:yyyy-MM-dd HH:mm:ss zzz}\r\n消息类型：{RawChatSource.TypeName(message.RawType)}\r\n消息编号：{message.ServerId} / 本地 {message.LocalId}\r\n来源：{message.SourceDatabase}\r\n\r\n【消息原文】\r\n{item.Text}\r\n\r\n{item.Note}".ReplaceLineEndings(Environment.NewLine) :
            $"{item.TargetName}\r\n旧版 OCR 记录，采集于 {item.ObservedAt:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n{item.Note}\r\n\r\n{item.Text}".ReplaceLineEndings(Environment.NewLine);
        picture.Image?.Dispose(); picture.Image = null;
        if (item != null && !string.IsNullOrEmpty(item.ImagePath) && File.Exists(store.ImageFile(item.ImagePath)))
        {
            using var image = Image.FromFile(store.ImageFile(item.ImagePath)); picture.Image = new Bitmap(image);
        }
    }
    private async void AddTarget(object? sender, EventArgs e)
    {
        await PauseAndDrain();
        try
        {
            using var dialog = new RawBindDialog(recorder);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                var existing = store.Targets().Select(t => t.Id).ToHashSet();
                foreach (var target in dialog.Results.Where(t => !existing.Contains(t.Id))) store.SaveTarget(target);
                RefreshTargets();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法添加会话"); }
    }
    private async void RemoveTarget(object? sender, EventArgs e)
    {
        if (targets.CurrentRow?.Tag is not string id) return;
        await PauseAndDrain(); store.RemoveTarget(id); RefreshTargets();
    }
    private async void Toggle(object? sender, EventArgs e)
    {
        if (timer.Enabled) { await PauseAndDrain(); return; }
        if (store.Targets().Count == 0) { MessageBox.Show(this, "请先选择需要记录的群或联系人。", "尚未绑定会话"); return; }
        cancellation.Dispose(); cancellation = new(); recorder.Resume();
        timer.Start(); start.Text = "暂停记录"; once.Enabled = false; badge.Text = "正在记录"; badge.ForeColor = Color.FromArgb(17, 127, 91);
        await Poll();
    }
    private async Task Poll()
    {
        if (exporting || closing || recorder.IsBusy) return;
        if (store.Targets().Count == 0) { badge.Text = "请先添加会话"; return; }
        if (cancellation.IsCancellationRequested) { cancellation.Dispose(); cancellation = new(); recorder.Resume(); }
        try { await recorder.PollAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { timer.Stop(); start.Text = "开始记录"; once.Enabled = true; badge.Text = "记录已暂停：" + ex.Message; }
    }
    private async Task PauseAndDrain()
    {
        timer.Stop(); cancellation.Cancel(); start.Text = "开始记录"; once.Enabled = true; badge.Text = "已暂停"; badge.ForeColor = Color.DimGray;
        while (recorder.IsBusy) await Task.Delay(50);
        foreach (DataGridViewRow row in targets.Rows) row.Cells[2].Value = "已暂停 · 点击开始记录可恢复";
    }
    private void MakeRequirement(object? sender, EventArgs e)
    {
        if (captures.CurrentRow?.Tag is not CaptureRecord source) return;
        using var dialog = new RequirementDialog(source);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } item) { store.SaveRequirement(item); RefreshRecords(); }
    }
    private void EditRequirement(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || requirements.Rows[e.RowIndex].Tag is not Requirement item) return;
        var source = store.Captures().FirstOrDefault(c => c.Id == item.CaptureId);
        if (source == null) return;
        using var dialog = new RequirementDialog(source, item);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Result is { } edited) { store.SaveRequirement(edited); RefreshRecords(); }
    }
    private async void Export(object? sender, EventArgs e)
    {
        await PauseAndDrain();
        using var dialog = new FolderBrowserDialog { Description = "选择导出位置，将在其中新建带时间的导出文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string folder = Path.Combine(dialog.SelectedPath, "微信需求导出_" + DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6]);
        exporting = true; Enabled = false;
        try { await Task.Run(() => Exporter.Export(store, folder)); OpenFolder(folder); badge.Text = "导出完成 · 采集已暂停"; }
        catch (Exception ex) { MessageBox.Show(this, "导出未完成：" + ex.Message, "导出失败"); }
        finally { exporting = false; Enabled = true; }
    }
    private static void OpenFolder(string folder) => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    private async void ClosingAsync(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing || exporting) return;
        closing = true; Enabled = false; await PauseAndDrain(); allowClose = true; Close();
    }
}
