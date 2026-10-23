using System.Security.Cryptography;
using System.Text;

namespace WeChatNotes;

internal sealed class RawBindDialog : Form
{
    private readonly RawRecorder recorder;
    private readonly ComboBox accounts = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly TextBox filter = new() { Dock = DockStyle.Fill, PlaceholderText = "搜索群名称或联系人" };
    private readonly CheckedListBox sessions = new() { Dock = DockStyle.Fill, CheckOnClick = true, DisplayMember = "DisplayName", IntegralHeight = false };
    private readonly ComboBox history = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "保持微信登录，点击“读取会话列表”。" };
    private readonly Button connect = new() { Text = "读取会话列表", AutoSize = true };
    private readonly Button save = new() { Text = "添加选中会话", AutoSize = true, Enabled = false };
    private readonly Button browse = new() { Text = "选择目录", Dock = DockStyle.Fill };
    private readonly HashSet<string> selected = [];
    private List<ChatSession> all = [];
    private readonly CancellationTokenSource cancellation = new();
    private bool busy;
    internal List<Target> Results { get; } = [];
    internal RawBindDialog(RawRecorder recorder)
    {
        this.recorder = recorder;
        Text = "选择需要记录的群或联系人"; Size = new(820, 660); MinimumSize = new(680, 520);
        Font = new("Microsoft YaHei UI", 10); StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(22), ColumnCount = 1, RowCount = 7 };
        foreach (var style in new RowStyle[] { new(SizeType.Absolute, 40), new(SizeType.Absolute, 42), new(SizeType.Absolute, 42), new(SizeType.Percent, 100), new(SizeType.Absolute, 48), new(SizeType.Absolute, 42), new(SizeType.Absolute, 48) }) layout.RowStyles.Add(style);
        layout.Controls.Add(new Label { Text = "选择会话后自动记录原始消息文字，微信窗口可最小化。", AutoSize = true }, 0, 0);
        var source = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        source.ColumnStyles.Add(new(SizeType.Percent, 100)); source.ColumnStyles.Add(new(SizeType.Absolute, 110));
        source.Controls.Add(accounts, 0, 0);
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "选择微信账号的 db_storage 文件夹", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) accounts.Text = dialog.SelectedPath; };
        source.Controls.Add(browse, 1, 0); layout.Controls.Add(source, 0, 1);
        layout.Controls.Add(filter, 0, 2); layout.Controls.Add(sessions, 0, 3);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 8, 0, 0) };
        options.Controls.Add(new Label { Text = "首次导入范围", AutoSize = true, Padding = new(0, 5, 6, 0) });
        history.Items.AddRange(["从添加时开始", "最近 24 小时", "最近 7 天", "全部本地历史"]); history.SelectedIndex = 0; options.Controls.Add(history);
        layout.Controls.Add(options, 0, 4); layout.Controls.Add(status, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(save); buttons.Controls.Add(connect); layout.Controls.Add(buttons, 0, 6); Controls.Add(layout);
        accounts.Items.AddRange(RawChatSource.Accounts()); if (accounts.Items.Count > 0) accounts.SelectedIndex = 0;
        accounts.TextChanged += (_, _) => { all.Clear(); selected.Clear(); sessions.Items.Clear(); save.Enabled = false; };
        filter.TextChanged += (_, _) => Populate();
        sessions.ItemCheck += (_, e) =>
        {
            if (sessions.Items[e.Index] is ChatSession item) { if (e.NewValue == CheckState.Checked) selected.Add(item.Username); else selected.Remove(item.Username); }
        };
        connect.Click += async (_, _) => await Connect();
        save.Click += (_, _) => Save();
        FormClosing += (_, e) => { if (busy) { cancellation.Cancel(); e.Cancel = true; status.Text = "正在取消，请稍后关闭。"; } };
        FormClosed += (_, _) => cancellation.Dispose();
    }
    private void Populate()
    {
        sessions.BeginUpdate(); sessions.Items.Clear();
        foreach (var item in all.Where(s => (s.DisplayName + s.Username).Contains(filter.Text, StringComparison.OrdinalIgnoreCase))) sessions.Items.Add(item, selected.Contains(item.Username));
        sessions.EndUpdate();
    }
    private async Task Connect()
    {
        if (busy) return;
        busy = true; connect.Enabled = save.Enabled = accounts.Enabled = browse.Enabled = false;
        try
        {
            string root = accounts.Text.Trim();
            var progress = new Progress<string>(s => { if (!IsDisposed) status.Text = s; });
            all = await Task.Run(() => recorder.Source(root).Sessions(s => ((IProgress<string>)progress).Report(s), cancellation.Token));
            selected.Clear(); Populate(); save.Enabled = true; status.Text = $"已读取 {all.Count} 个会话，请勾选需要记录的群或联系人。";
        }
        catch (OperationCanceledException) { status.Text = "已取消，请关闭窗口后重试。"; }
        catch (Exception ex) { status.Text = "连接未完成"; MessageBox.Show(this, ex.Message, "无法读取会话"); }
        finally { busy = false; connect.Enabled = accounts.Enabled = browse.Enabled = true; }
    }
    private void Save()
    {
        if (selected.Count == 0) { status.Text = "请至少勾选一个会话。"; return; }
        string root = Path.GetFullPath(accounts.Text.Trim());
        long now = DateTimeOffset.Now.ToUnixTimeSeconds();
        long from = history.SelectedIndex switch { 1 => now - 86400, 2 => now - 7 * 86400, 3 => 0, _ => now };
        foreach (var session in all.Where(s => selected.Contains(s.Username)))
        {
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant() + "|" + session.Username))).ToLowerInvariant();
            Results.Add(new(id, session.DisplayName, "", 0,0,0,0,0,0,root,session.Username,from));
        }
        DialogResult = DialogResult.OK;
    }
}
