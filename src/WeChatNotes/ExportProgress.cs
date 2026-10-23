namespace WeChatNotes;

public sealed record ExportProgress(string Stage, int Completed = 0, int Total = 0)
{
    public int? Percent => Total > 0 ? (int)Math.Clamp((long)Completed * 100 / Total, 0, 100) : null;
}

internal sealed class ExportProgressReporter(IProgress<ExportProgress>? progress) : IProgress<ExportProgress>
{
    private string? stage;
    private long lastReport;
    public void Report(ExportProgress value)
    {
        if (progress == null) return;
        long now = Environment.TickCount64;
        // Keep large exports from flooding the UI thread with one callback per message.
        if (value.Stage == stage && value.Completed < value.Total && now - lastReport < 100) return;
        stage = value.Stage; lastReport = now; progress.Report(value);
    }
}
