using System.Diagnostics;

namespace WeChatNotes;

internal sealed class ExportProgressDialog : Form
{
    private readonly Label stage = new() { Text = "正在准备导出…", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label counts = new() { Text = "正在读取当前会话的全部已采集记录", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label elapsed = new() { Dock = DockStyle.Fill, ForeColor = Color.DimGray };
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Stopwatch watch = new();
    private bool finished;
    internal Exception? Failure { get; private set; }

    internal ExportProgressDialog(Action<IProgress<ExportProgress>> export)
    {
        Text = "正在导出聊天记录"; ClientSize = new(530, 230); Font = new("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ControlBox = false; ShowInTaskbar = false;
        BackColor = Color.FromArgb(246, 248, 250);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 5 };
        foreach (int height in new[] { 42, 36, 32, 32, 40 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        stage.Font = new(Font, FontStyle.Bold);
        layout.Controls.Add(stage, 0, 0); layout.Controls.Add(bar, 0, 1); layout.Controls.Add(counts, 0, 2);
        layout.Controls.Add(elapsed, 0, 3);
        layout.Controls.Add(new Label { Text = "导出完成后将自动打开文件夹。", Dock = DockStyle.Fill, ForeColor = Color.DimGray }, 0, 4);
        Controls.Add(layout);
        timer.Tick += (_, _) => UpdateElapsed();
        Shown += async (_, _) =>
        {
            watch.Start(); UpdateElapsed(); timer.Start();
            var progress = new Progress<ExportProgress>(SetProgress);
            try { await Task.Run(() => export(progress)); }
            catch (Exception ex) { Failure = ex; }
            finally
            {
                finished = true; timer.Stop(); watch.Stop();
                DialogResult = Failure == null ? DialogResult.OK : DialogResult.Abort;
                Close();
            }
        };
        FormClosing += (_, e) => { if (!finished) e.Cancel = true; };
    }

    internal void SetProgress(ExportProgress progress)
    {
        if (finished || IsDisposed) return;
        stage.Text = progress.Stage;
        if (progress.Percent is int percent)
        {
            bar.Style = ProgressBarStyle.Continuous; bar.Value = percent;
            counts.Text = $"本步骤：{progress.Completed:N0} / {progress.Total:N0}（{percent}%）";
        }
        else
        {
            bar.Style = ProgressBarStyle.Marquee;
            counts.Text = "正在处理，请稍候…";
        }
        UpdateElapsed();
    }
    private void UpdateElapsed() => elapsed.Text = $"已用时 {(int)watch.Elapsed.TotalMinutes:00}:{watch.Elapsed.Seconds:00}";
    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
