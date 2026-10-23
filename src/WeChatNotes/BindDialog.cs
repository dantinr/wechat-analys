namespace WeChatNotes;

public sealed class CropPreview : Control
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Bitmap? Image { get; set; }
    public Rectangle Selection { get; private set; }
    private Point start;
    public event Action? SelectionChanged;
    public CropPreview() { DoubleBuffered = true; BackColor = Color.FromArgb(38, 43, 50); Cursor = Cursors.Cross; }
    private RectangleF ImageBounds
    {
        get
        {
            if (Image == null) return RectangleF.Empty;
            float scale = Math.Min((float)Width / Image.Width, (float)Height / Image.Height);
            return new((Width - Image.Width * scale) / 2, (Height - Image.Height * scale) / 2, Image.Width * scale, Image.Height * scale);
        }
    }
    private Point ToImage(Point point)
    {
        var bounds = ImageBounds;
        return new(Math.Clamp((int)((point.X - bounds.X) * Image!.Width / bounds.Width), 0, Image.Width),
            Math.Clamp((int)((point.Y - bounds.Y) * Image.Height / bounds.Height), 0, Image.Height));
    }
    public void Reset() { Selection = Rectangle.Empty; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Image == null || e.Button != MouseButtons.Left || !ImageBounds.Contains(e.Location)) return;
        start = ToImage(e.Location); Capture = true; Selection = Rectangle.Empty; SelectionChanged?.Invoke();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!Capture || Image == null) return;
        Point end = ToImage(e.Location);
        Selection = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e) { Capture = false; SelectionChanged?.Invoke(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Image == null) return;
        var bounds = ImageBounds;
        e.Graphics.DrawImage(Image, bounds);
        if (!Selection.IsEmpty)
        {
            float scale = bounds.Width / Image.Width;
            using var pen = new Pen(Color.FromArgb(22, 187, 115), 3);
            e.Graphics.DrawRectangle(pen, bounds.X + Selection.X * scale, bounds.Y + Selection.Y * scale, Selection.Width * scale, Selection.Height * scale);
        }
    }
}

public sealed class BindDialog : Form
{
    private readonly ComboBox windows = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
    private readonly TextBox name = new() { Width = 190, PlaceholderText = "会话名称" };
    private readonly CropPreview preview = new() { Dock = DockStyle.Fill };
    private readonly TextBox text = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly Button save = new() { Text = "保存绑定", AutoSize = true, Enabled = false };
    private readonly Label status = new() { AutoSize = true, Text = "选择窗口后，点击读取画面。" };
    private readonly WindowCapture capture;
    private readonly LocalOcr ocr;
    private nint boundHandle;
    private string boundTitle = "";
    private bool busy;
    public Target? Result { get; private set; }

    public BindDialog()
    {
        Text = "添加需要记录的会话"; Width = 1120; Height = 820; MinimumSize = new(900, 650); StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10);
        capture = new(); ocr = new();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(16) };
        layout.RowStyles.Add(new(SizeType.Absolute, 66)); layout.RowStyles.Add(new(SizeType.Absolute, 46));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 45));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "1. 在微信会话列表中双击目标群/联系人，将它打开为独立窗口。\n2. 读取画面后，在左侧拖框选择消息列表（避开输入框和标题），识别预览后保存。", AutoSize = false }, 0, 0);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var refresh = new Button { Text = "刷新窗口", AutoSize = true };
        var read = new Button { Text = "读取画面", AutoSize = true };
        top.Controls.AddRange([windows, refresh, name, read]); layout.Controls.Add(top, 0, 1);
        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1040, 540), SplitterDistance = 660 };
        split.Panel1.Controls.Add(preview); split.Panel2.Controls.Add(text); layout.Controls.Add(split, 0, 2);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var recognize = new Button { Text = "识别预览", AutoSize = true };
        bottom.Controls.AddRange([recognize, save, status]); layout.Controls.Add(bottom, 0, 3); Controls.Add(layout);
        refresh.Click += (_, _) => RefreshWindows();
        windows.SelectedIndexChanged += (_, _) =>
        {
            save.Enabled = false; preview.Image?.Dispose(); preview.Image = null; preview.Reset(); text.Clear(); boundHandle = 0;
            if (windows.SelectedItem is ChatWindow window) name.Text = window.Title;
        };
        preview.SelectionChanged += () => save.Enabled = false;
        read.Click += async (_, _) =>
        {
            if (windows.SelectedItem is not ChatWindow window) { status.Text = "没有可用窗口，请先在微信中打开独立聊天窗口。"; return; }
            busy = true; read.Enabled = refresh.Enabled = windows.Enabled = recognize.Enabled = false; save.Enabled = false;
            try
            {
                var image = await capture.CaptureAsync(window.Handle);
                if (NativeWindows.Title(window.Handle) != window.Title) { image.Dispose(); throw new InvalidOperationException("窗口标题已改变，请刷新。"); }
                preview.Image?.Dispose(); preview.Image = image; preview.Reset();
                boundHandle = window.Handle; boundTitle = window.Title; text.Clear();
                status.Text = "请在画面上拖框选择消息列表。";
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; if (!IsDisposed) read.Enabled = refresh.Enabled = windows.Enabled = recognize.Enabled = true; }
        };
        recognize.Click += async (_, _) =>
        {
            if (preview.Image == null || preview.Selection.Width < 100 || preview.Selection.Height < 50) { status.Text = "请先拖框选择足够大的消息区域。"; return; }
            busy = true; read.Enabled = refresh.Enabled = windows.Enabled = recognize.Enabled = preview.Enabled = false;
            try
            {
                using var crop = preview.Image.Clone(preview.Selection, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                string content = await ocr.ReadAsync(crop);
                if (IsDisposed) return;
                text.Text = content.Replace("\n", Environment.NewLine); save.Enabled = !string.IsNullOrWhiteSpace(content);
                status.Text = save.Enabled ? "核对右侧识别结果后保存。" : "未识别到文字，请调整区域。";
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
            finally { busy = false; if (!IsDisposed) read.Enabled = refresh.Enabled = windows.Enabled = recognize.Enabled = preview.Enabled = true; }
        };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { status.Text = "请填写会话名称。"; return; }
            if (!NativeWindows.IsWindow(boundHandle) || NativeWindows.Title(boundHandle) != boundTitle) { save.Enabled = false; status.Text = "聊天窗口已变化，请重新读取。"; return; }
            var region = preview.Selection;
            Result = new(Guid.NewGuid().ToString("N"), name.Text.Trim(), boundTitle, preview.Image!.Width, preview.Image.Height, region.X, region.Y, region.Width, region.Height);
            DialogResult = DialogResult.OK;
        };
        FormClosed += (_, _) => { preview.Image?.Dispose(); capture.Dispose(); };
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        RefreshWindows();
    }

    private void RefreshWindows()
    {
        windows.Items.Clear(); windows.Items.AddRange(NativeWindows.Chats().Cast<object>().ToArray());
        if (windows.Items.Count > 0) windows.SelectedIndex = 0;
        else status.Text = "未找到独立聊天窗口，请双击微信会话后刷新。";
    }
}
