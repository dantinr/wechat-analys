namespace WeChatNotes;

public sealed class Recorder : IDisposable
{
    private readonly Store store;
    private readonly WindowCapture capture = new();
    private readonly LocalOcr ocr = new();
    private readonly Func<List<ChatWindow>> enumerateWindows;
    private readonly Dictionary<string, string> previous = new();
    private readonly HashSet<string> interrupted = new();
    private readonly HashSet<string> continued = new();
    private readonly HashSet<string> failures = new();
    private bool busy;
    public bool IsBusy => busy;
    public event Action<string, string>? Status;
    public event Action<CaptureRecord>? Recorded;
    public Recorder(Store store, Func<List<ChatWindow>>? enumerateWindows = null)
    {
        this.store = store;
        this.enumerateWindows = enumerateWindows ?? NativeWindows.Chats;
    }

    public void Resume()
    {
        foreach (var id in previous.Keys) continued.Add(id);
    }

    public async Task PollAsync(CancellationToken token)
    {
        if (busy) return;
        busy = true;
        try
        {
            var windows = enumerateWindows();
            foreach (var target in store.Targets())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var matching = windows.Where(w => w.Title == target.WindowTitle).ToList();
                    if (matching.Count != 1) throw new InvalidOperationException(matching.Count == 0 ? "未找到独立聊天窗口，请重新打开。" : "同名窗口不唯一，请关闭重复窗口。");
                    nint hwnd = matching[0].Handle;
                    if (NativeWindows.Title(hwnd) != target.WindowTitle) throw new InvalidOperationException("窗口标题已改变，请重新绑定。");
                    using var image = await capture.CaptureAsync(hwnd, token);
                    if (NativeWindows.Title(hwnd) != target.WindowTitle) throw new InvalidOperationException("采集期间会话发生变化，已跳过本次画面。");
                    if (image.Width != target.Width || image.Height != target.Height)
                        throw new InvalidOperationException("窗口尺寸或缩放已变化，请移除绑定后重新选择区域。");
                    using var crop = image.Clone(target.Region, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    string text = await ocr.ReadAsync(crop);
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("没有识别到文字，请检查聊天区域、窗口或桌面锁定状态。");
                    previous.TryGetValue(target.Id, out string? last);
                    bool gap = failures.Contains(target.Id) || continued.Contains(target.Id);
                    // Resume with identical content still records the gap instead of silently masking downtime.
                    if (last != null && TextDiff.Hash(last) == TextDiff.Hash(text) && !gap)
                    {
                        Status?.Invoke(target.Id, "记录中 · 画面无新文字");
                        continue;
                    }
                    var delta = TextDiff.Added(gap ? null : last, text);
                    string note = delta.Note;
                    if (interrupted.Add(target.Id)) note = "首次采集此会话（重启后重新建立基线）。" + note;
                    if (gap) note = "采集中断后恢复；中断期间可能有遗漏，本次保留整屏。" + note;
                    string id = Guid.NewGuid().ToString("N");
                    var time = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
                    var record = new CaptureRecord(id, target.Id, target.Name, time, text, delta.Text,
                        Path.Combine("screenshots", time.ToString("yyyy-MM-dd"), id + ".png"), note);
                    store.Add(record, crop);
                    previous[target.Id] = text;
                    failures.Remove(target.Id);
                    continued.Remove(target.Id);
                    Status?.Invoke(target.Id, "记录中 · " + time.ToString("HH:mm:ss"));
                    Recorded?.Invoke(record);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures.Add(target.Id);
                    Status?.Invoke(target.Id, "暂停此会话 · " + ex.Message);
                }
            }
        }
        finally { busy = false; }
    }
    public void Dispose() => capture.Dispose();
}
