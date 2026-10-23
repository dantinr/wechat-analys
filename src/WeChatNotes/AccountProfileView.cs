using System.Drawing.Drawing2D;

namespace WeChatNotes;

internal sealed class AccountProfileView : UserControl
{
    private readonly PictureBox avatar = new() { Dock = DockStyle.Right, Width = 64, SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "微信头像" };
    private readonly Label name = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.BottomRight, Padding = new(0, 0, 10, 5) };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.TopRight, ForeColor = Color.DimGray, Padding = new(0, 3, 10, 0) };
    private readonly ToolTip tooltip = new();
    private readonly Func<string, CancellationToken, WeChatProfile?> read;
    private CancellationTokenSource? pending;
    private string? root;
    private bool initialized;
    private int generation;

    internal AccountProfileView(Func<string, CancellationToken, WeChatProfile?>? read = null)
    {
        this.read = read ?? WeChatProfiles.Read;
        Width = 240; Height = 72; Padding = new(0, 4, 0, 4); AccessibleName = "当前微信用户";
        var labels = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        labels.RowStyles.Add(new(SizeType.Percent, 55)); labels.RowStyles.Add(new(SizeType.Percent, 45));
        labels.Controls.Add(name, 0, 0); labels.Controls.Add(status, 0, 1);
        Controls.Add(labels); Controls.Add(avatar);
        foreach (Control control in new Control[] { this, labels, name, status, avatar })
        {
            control.Cursor = Cursors.Hand;
            tooltip.SetToolTip(control, "当前会话所属的微信账号，点击刷新头像");
            control.Click += async (_, _) => await ShowAccountAsync(root, true);
        }
        Apply(null, "未选择微信账号");
    }

    internal async Task ShowAccountAsync(string? sourceRoot, bool refresh = false)
    {
        if (IsDisposed || Disposing || initialized && !refresh && string.Equals(root, sourceRoot, StringComparison.OrdinalIgnoreCase)) return;
        initialized = true; root = sourceRoot; int request = ++generation;
        pending?.Cancel(); pending = null;
        Apply(null, sourceRoot == null ? "未选择微信账号" : "正在读取头像…");
        if (sourceRoot == null) return;
        using var cancellation = new CancellationTokenSource(); pending = cancellation;
        try
        {
            var profile = await Task.Run(() => read(sourceRoot, cancellation.Token), cancellation.Token);
            if (!IsDisposed && !Disposing && request == generation)
                Apply(profile, profile == null ? "暂未读取到头像" : "当前会话的微信账号");
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!IsDisposed && !Disposing && request == generation) Apply(null, "暂未读取到头像");
        }
        finally { if (ReferenceEquals(pending, cancellation)) pending = null; }
    }

    private void Apply(WeChatProfile? profile, string message)
    {
        Image? image = DecodeAvatar(profile?.Avatar);
        bool hasAvatar = image != null;
        image ??= Placeholder();
        var previous = avatar.Image; avatar.Image = image; previous?.Dispose();
        name.Text = profile?.Name ?? "微信用户";
        status.Text = profile != null && !hasAvatar ? "暂无本地头像 · 点击刷新" : message;
        avatar.AccessibleDescription = hasAvatar ? "当前微信账号的本地头像" : "默认头像";
        tooltip.SetToolTip(name, name.Text + "\n点击刷新微信头像");
    }

    internal static Bitmap? DecodeAvatar(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0 || bytes.Length > WeChatProfiles.MaximumAvatarBytes) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (image.Width > 2048 || image.Height > 2048) return null;
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { return null; }
    }

    private static Bitmap Placeholder()
    {
        var image = new Bitmap(64, 64);
        using var g = Graphics.FromImage(image); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Color.FromArgb(230, 236, 240));
        using var person = new SolidBrush(Color.FromArgb(153, 167, 179));
        g.FillEllipse(background, 1, 1, 62, 62); g.FillEllipse(person, 23, 12, 18, 18); g.FillEllipse(person, 13, 34, 38, 25);
        return image;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { generation++; pending?.Cancel(); tooltip.Dispose(); avatar.Image?.Dispose(); avatar.Image = null; }
        base.Dispose(disposing);
    }
}
