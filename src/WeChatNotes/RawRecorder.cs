namespace WeChatNotes;

internal sealed class RawRecorder(Store store) : IDisposable
{
    private readonly Dictionary<string, RawChatSource> sources = new(StringComparer.OrdinalIgnoreCase);
    internal bool IsBusy { get; private set; }
    internal event Action<string, string>? Status;
    internal event Action? Recorded;
    internal string[] KnownRoots() => store.TargetSources().Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal RawChatSource Source(string root)
    {
        root = Path.GetFullPath(root);
        if (!sources.TryGetValue(root, out var source)) sources[root] = source = new(root);
        return source;
    }
    internal void Resume() { }
    internal async Task PollAsync(CancellationToken token)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var targets = store.Targets();
            foreach (var old in targets.Where(t => t.SourceRoot == null)) Status?.Invoke(old.Id, "旧版画面绑定，仅保留历史记录；请添加原文会话");
            foreach (var group in targets.Where(t => t.SourceRoot != null && t.ConversationId != null).GroupBy(t => t.SourceRoot!, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var selected = group.ToList();
                    var records = await Task.Run(() => Source(group.Key).Read(selected, token), token);
                    token.ThrowIfCancellationRequested();
                    int added = store.AddMessages(records);
                    foreach (var target in selected) Status?.Invoke(target.Id, $"已同步 {DateTimeOffset.Now:HH:mm:ss} · 本次读取 {records.Count(r => r.TargetId == target.Id)} 条");
                    if (added > 0) Recorded?.Invoke();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { foreach (var target in group) Status?.Invoke(target.Id, "本次未同步 · " + ex.Message); }
            }
        }
        finally { IsBusy = false; }
    }
    public void Dispose() { foreach (var source in sources.Values) source.Dispose(); }
}
