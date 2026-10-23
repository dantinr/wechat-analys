namespace WeChatNotes;

internal static class ConversationOptionsSelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        string data = Path.Combine(root, "conversation-options-store");
        using var store = new Store(data);
        string source = Path.Combine(root, "synthetic-options-account", "db_storage");
        Target Target(string id) => new(id, "合成会话 " + id, "", 0, 0, 0, 0, 0, 0, source, id + "@chatroom");
        foreach (string id in new[] { "a", "b", "c" })
        {
            store.SaveTarget(Target(id));
            store.AddMessages([new(id + "-message", id, "合成会话 " + id, DateTimeOffset.Now, "合成原文", "合成原文", "", "合成关闭测试")]);
        }
        using var host = new Form { Width = 780, Height = 600, Opacity = 0, ShowInTaskbar = false };
        using var view = new ConversationRecords(store);
        host.Controls.Add(view); host.Show(); view.RefreshRecords(); view.SelectConversation("a");
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var tabs = Descendants(view).OfType<TabControl>().Single();
        string? closed = null;
        view.CloseRequested += id => { closed = id; store.CloseConversation(id); view.RefreshRecords(); };
        bool CloseTab(string id)
        {
            int index = tabs.TabPages.Cast<TabPage>().Select((page, number) => (page, number)).Single(p => (string?)p.page.Tag == id).number;
            var rectangle = tabs.GetTabRect(index);
            var menu = view.ShowConversationMenu(new(rectangle.Left + rectangle.Width / 2, rectangle.Top + rectangle.Height / 2))!;
            menu.Opacity = 0;
            bool unchanged = view.SelectedConversationId == "a";
            menu.Items[0].PerformClick(); menu.Close();
            return unchanged;
        }
        bool stayed = CloseTab("b");
        check("右键关闭未选中会话准确移除绑定且保留当前标签和消息", stayed && closed == "b" && view.SelectedConversationId == "a" && !view.ConversationIds.Contains("b") && store.Targets().All(t => t.Id != "b") && store.Captures(targetId: "b").Count == 1 && store.TargetSources()["b"] == source);
        view.RefreshRecords();
        using (var reopened = new Store(data))
            check("关闭的历史标签在刷新和重启后保持关闭且原文仍保留", !view.ConversationIds.Contains("b") && reopened.CaptureConversations().All(c => c.Id != "b") && reopened.Captures(targetId: "b").Single().Text == "合成原文");
        store.SaveTarget(Target("b")); view.RefreshRecords(); view.SelectConversation("b");
        check("重新添加会话恢复历史标签且不会复制原消息", view.SelectedConversationId == "b" && view.SelectedRecord?.Id == "b-message" && store.Captures(targetId: "b").Count == 1 && store.Targets().Count == 3);
        CloseTab("b");
        check("关闭当前会话后选择剩余会话并更新来源", view.SelectedConversationId == "a" && view.SelectedRecord?.TargetId == "a" && view.TotalCount == 1);
        check("右键列表正文不会触发标签关闭菜单", view.ShowConversationMenu(new(5, tabs.ClientSize.Height - 5)) == null && view.ConversationIds.Length == 2);
        CloseTab("a"); CloseTab("c");
        check("关闭最后一个会话清空显示但保留所有历史和附件来源", view.SelectedConversationId == null && view.SelectedRecord == null && view.TotalCount == 0 && view.VisibleCount == 0 && store.Targets().Count == 0 && store.Captures().Count == 3 && store.TargetSources().Count == 3);

        using (var recorder = new RawRecorder(store))
        using (var dialog = new RawBindDialog(recorder))
        {
            // Inspect the options without showing the dialog or discovering personal accounts.
            var history = Descendants(dialog).OfType<ComboBox>().Single(c => c.AccessibleName == "首次导入范围");
            long now = new DateTimeOffset(2026, 1, 5, 10, 30, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds();
            check("首次导入范围默认选择全部本地历史", history.SelectedItem is ImportRange { Label: "全部本地历史" } range && range.FromUnix(now) == 0);
            history.SelectedIndex = history.Items.Cast<ImportRange>().Select((r, i) => (r, i)).Single(p => p.r.Label == "最近 15 天").i;
            long fifteen = ((ImportRange)history.SelectedItem!).FromUnix(now);
            history.SelectedIndex = history.Items.Cast<ImportRange>().Select((r, i) => (r, i)).Single(p => p.r.Label == "最近 30 天").i;
            long thirty = ((ImportRange)history.SelectedItem!).FromUnix(now);
            check("15天和30天选项按添加时间跨年计算正确起点", fifteen == new DateTimeOffset(2025, 12, 21, 10, 30, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds() && thirty == new DateTimeOffset(2025, 12, 6, 10, 30, 0, TimeSpan.FromHours(8)).ToUnixTimeSeconds());
        }
        CheckTargetMenu(root, check);
        host.Close();
    }

    private static void CheckTargetMenu(string root, Action<string, bool> check)
    {
        string data = Path.Combine(root, "target-menu-store");
        using var store = new Store(data);
        // Legacy targets exercise scoped reads without discovering accounts or accessing WeChat.
        foreach (string id in new[] { "a", "b", "c" })
        {
            store.SaveTarget(new(id, "合成会话 " + id, "", 0, 0, 0, 0, 0, 0));
            store.AddMessages([new(id + "-message", id, "合成会话 " + id, DateTimeOffset.Now, id + "记录", id + "记录", "", "合成菜单测试")]);
        }
        using var form = new MainForm(store) { Opacity = 0, ShowInTaskbar = false };
        form.Show();
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var controls = Descendants(form).ToArray();
        var grid = controls.OfType<DataGridView>().Single(c => c.AccessibleName == "记录会话");
        // Leave actual empty body space even with the fixed-height target list.
        foreach (DataGridViewRow row in grid.Rows) row.Height = 22;
        var records = controls.OfType<ConversationRecords>().Single();
        var search = controls.OfType<TextBox>().Single(c => c.AccessibleName == "会话搜索");
        ContextMenuStrip Menu(string id)
        {
            var row = grid.Rows.Cast<DataGridViewRow>().Single(r => (string?)r.Tag == id);
            grid.FirstDisplayedScrollingRowIndex = row.Index;
            var rect = grid.GetCellDisplayRectangle(1, row.Index, false);
            var menu = form.ShowTargetMenu(new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2))!;
            menu.Opacity = 0;
            return menu;
        }
        void Click(string id, string action)
        {
            var menu = Menu(id);
            menu.Items.Cast<ToolStripItem>().Single(item => item.Text == action).PerformClick(); menu.Close();
        }
        check("会话列表右键表头和空白处不显示操作菜单", form.ShowTargetMenu(new(10, 5)) == null && form.ShowTargetMenu(new(10, grid.ClientSize.Height - 3)) == null);
        var menu = Menu("b");
        check("右键准确选中点击的会话并显示读取搜索移除功能", (string?)grid.CurrentRow?.Tag == "b" && menu.Items.Cast<ToolStripItem>().Select(i => i.Text).SequenceEqual(["读取记录", "搜索", "移除对话"]) && records.SelectedConversationId == "a");
        menu.Close(); search.Text = "不匹配的搜索词";
        Click("b", "读取记录");
        check("读取记录切换到右键会话并清除旧搜索条件", records.SelectedConversationId == "b" && search.Text == "" && records.SelectedRecord?.Id == "b-message" && form.Enabled);
        check("读取记录仅同步所选会话且不读取其他会话", grid.Rows.Cast<DataGridViewRow>().Single(r => (string?)r.Tag == "b").Cells["status"].Value?.ToString()?.Contains("旧版画面绑定") == true && grid.Rows.Cast<DataGridViewRow>().Where(r => (string?)r.Tag != "b").All(r => r.Cells["status"].Value?.ToString()?.Contains("已暂停") == true));
        search.Text = "记录"; Click("a", "搜索");
        check("搜索菜单切换会话并聚焦全选关键词且结果隔离", records.SelectedConversationId == "a" && search.Focused && search.SelectedText == "记录" && records.VisibleCount == 1 && records.SelectedRecord?.TargetId == "a");
        Click("c", "移除对话");
        check("移除未打开对话保留当前会话及历史并连续编号", records.SelectedConversationId == "a" && !records.ConversationIds.Contains("c") && store.Captures(targetId: "c").Count == 1 && grid.Rows.Cast<DataGridViewRow>().Select(r => (int)r.Cells["number"].Value!).SequenceEqual([1, 2]) && form.Enabled);
        Click("a", "移除对话"); Click("b", "移除对话");
        check("列表移除最后一个对话清空标签和预览且保留消息", records.SelectedConversationId == null && records.SelectedRecord == null && grid.Rows.Count == 0 && store.Captures().Count == 3 && form.Enabled);
        using (var reopened = new Store(data))
            check("列表移除状态重启后保留且重新添加可恢复原消息", reopened.Targets().Count == 0 && reopened.CaptureConversations().Count == 0 && Restore());
        bool Restore()
        {
            store.SaveTarget(new("b", "合成会话 b", "", 0, 0, 0, 0, 0, 0));
            return store.CaptureConversations().Single().Id == "b" && store.Captures(targetId: "b").Single().Id == "b-message";
        }
        form.Close();
    }
}
