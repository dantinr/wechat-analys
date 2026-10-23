namespace WeChatNotes;

internal sealed class ConversationRecords : UserControl
{
    private readonly Store store;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, ShowToolTips = true };
    private readonly Dictionary<string, TabPage> pages = new(StringComparer.Ordinal);
    private bool updating;
    private string search = "";

    internal event Action? SelectionChanged;
    internal CaptureRecord? SelectedRecord => CurrentGrid?.CurrentRow?.Tag as CaptureRecord;
    internal string? SelectedConversationId => tabs.SelectedTab?.Tag as string;
    internal int RecentCount { get; private set; }
    internal int VisibleCount => CurrentGrid?.Rows.Count ?? 0;
    internal DataGridView? CurrentGrid => tabs.SelectedTab?.Controls.OfType<DataGridView>().Single();
    internal string[] ConversationIds => pages.Keys.Where(id => id.Length > 0).ToArray();

    internal ConversationRecords(Store store)
    {
        this.store = store; Dock = DockStyle.Fill;
        Controls.Add(tabs); AddPage("", "全部");
        tabs.SelectedIndexChanged += (_, _) => { if (!updating) LoadCurrent(); };
    }

    private void AddPage(string id, string name)
    {
        var page = new TabPage(name) { Tag = id.Length == 0 ? null : id, ToolTipText = name };
        var grid = MainForm.Grid();
        grid.Columns.Add("time", "发送时间"); grid.Columns.Add("name", "会话");
        grid.Columns.Add("nickname", "群昵称"); grid.Columns.Add("text", "消息原文");
        grid.Columns[0].FillWeight = 27; grid.Columns[1].FillWeight = 22;
        grid.Columns[2].FillWeight = 25; grid.Columns[3].FillWeight = 65;
        grid.SelectionChanged += (_, _) => { if (!updating && tabs.SelectedTab == page) SelectionChanged?.Invoke(); };
        page.Controls.Add(grid); pages.Add(id, page); tabs.TabPages.Add(page);
    }

    internal void RefreshRecords()
    {
        string selected = SelectedConversationId ?? "";
        var bound = store.Targets().ToDictionary(t => t.Id, t => t.Name, StringComparer.Ordinal);
        var conversations = bound.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (id, name) in store.CaptureConversations()) conversations.TryAdd(id, name + "（已移除）");
        updating = true; tabs.SuspendLayout();
        try
        {
            foreach (string id in pages.Keys.Where(id => id.Length > 0 && !conversations.ContainsKey(id)).ToArray())
            { var page = pages[id]; tabs.TabPages.Remove(page); pages.Remove(id); page.Dispose(); }
            foreach (var (id, name) in conversations)
            {
                if (!pages.TryGetValue(id, out var page)) AddPage(id, name);
                else { page.Text = name; page.ToolTipText = name; }
            }
            tabs.SelectedTab = pages.GetValueOrDefault(selected, pages[""]);
        }
        finally { tabs.ResumeLayout(); updating = false; }
        LoadCurrent();
    }

    internal bool SelectConversation(string? id)
    {
        if (!pages.TryGetValue(id ?? "", out var page)) return false;
        tabs.SelectedTab = page; return true;
    }

    internal void Search(string text) { search = text; LoadCurrent(); }

    private void LoadCurrent()
    {
        if (CurrentGrid is not { } grid) return;
        string? selected = SelectedRecord?.Id;
        var records = store.Captures(500, SelectedConversationId);
        RecentCount = records.Count;
        updating = true;
        try
        {
            grid.Rows.Clear();
            foreach (var item in records.AsEnumerable().Reverse().Where(c => string.IsNullOrWhiteSpace(search) ||
                (c.Text + "\n" + c.TargetName + "\n" + c.Message?.Sender + "\n" + c.Message?.GroupNickname).Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                int index = grid.Rows.Add((item.Message?.SentAt ?? item.ObservedAt).ToString("MM-dd HH:mm:ss"), item.TargetName,
                    string.IsNullOrWhiteSpace(item.Message?.GroupNickname) ? "—" : item.Message.GroupNickname, item.AddedText.ReplaceLineEndings(" "));
                grid.Rows[index].Tag = item;
            }
            var row = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as CaptureRecord)?.Id == selected)
                ?? grid.Rows.Cast<DataGridViewRow>().FirstOrDefault();
            if (row != null) { grid.CurrentCell = row.Cells[0]; row.Selected = true; }
        }
        finally { updating = false; }
        SelectionChanged?.Invoke();
    }
}
