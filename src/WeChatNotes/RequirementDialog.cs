namespace WeChatNotes;

public sealed class RequirementDialog : Form
{
    public Requirement? Result { get; private set; }
    public RequirementDialog(CaptureRecord source, Requirement? existing = null)
    {
        Text = "整理为需求 · " + source.TargetName; Size = new(740, 570); MinimumSize = new(600, 450);
        StartPosition = FormStartPosition.CenterParent; Font = new("Microsoft YaHei UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 7, ColumnCount = 1 };
        layout.RowStyles.Add(new(SizeType.Absolute, 28)); layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 28)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Absolute, 45));
        layout.RowStyles.Add(new(SizeType.Absolute, 42));
        var title = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "填写需求名称" };
        var notes = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = source.AddedText.Replace("\n", Environment.NewLine) };
        var status = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        status.Items.AddRange(["待确认", "已确认", "进行中", "已完成", "暂缓"]); status.SelectedIndex = 0;
        if (existing != null) { title.Text = existing.Title; notes.Text = existing.Notes; status.SelectedItem = existing.Status; }
        layout.Controls.Add(new Label { Text = "需求名称", AutoSize = true }, 0, 0); layout.Controls.Add(title, 0, 1);
        layout.Controls.Add(new Label { Text = "需求说明（请核对原文后编辑）", AutoSize = true }, 0, 2); layout.Controls.Add(notes, 0, 3);
        layout.Controls.Add(status, 0, 4);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = $"来源：{source.TargetName} · {source.Message?.Sender} · {(source.Message?.SentAt ?? source.ObservedAt):yyyy-MM-dd HH:mm:ss}\n保存后可在导出的需求文档中返回来源消息。" }, 0, 5);
        var save = new Button { Text = "保存需求", Width = 120, Dock = DockStyle.Right };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(title.Text)) { title.Focus(); return; }
            Result = new(existing?.Id ?? Guid.NewGuid().ToString("N"), title.Text.Trim(), notes.Text.Trim(), status.Text, source.Id, existing?.CreatedAt ?? DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)));
            DialogResult = DialogResult.OK;
        };
        layout.Controls.Add(save, 0, 6); Controls.Add(layout); AcceptButton = save;
    }
}
