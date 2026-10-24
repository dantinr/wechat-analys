using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Whisper.net.Logger;

namespace WeChatNotes;

internal sealed record GpuSpeechRequest(string? ModelPath = null, string? AudioPath = null);
internal sealed record GpuSpeechResponse(bool Ready, VoiceTranscript? Transcript = null);

internal sealed class GpuSpeechWorker : IGpuSpeechWorker
{
    private readonly Process process;
    internal int ProcessId => process.Id;

    internal GpuSpeechWorker(string modelPath)
    {
        var start = CreateStartInfo();
        process = Process.Start(start) ?? throw new IOException("无法启动 GPU 识别。");
        _ = DrainErrors(process);
        try
        {
            Send(new(ModelPath: modelPath));
            if (!Receive(TimeSpan.FromMinutes(2)).Ready) throw new IOException("GPU 不可用。");
        }
        catch { Dispose(); throw; }
    }

    internal static ProcessStartInfo CreateStartInfo()
    {
        string executable = Environment.ProcessPath ?? throw new IOException("找不到识别程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Ripper.dll"));
        start.ArgumentList.Add("--speech-worker");
        return start;
    }

    private static async Task DrainErrors(Process worker)
    {
        try { await worker.StandardError.BaseStream.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void Send(GpuSpeechRequest request)
    {
        process.StandardInput.WriteLine(JsonSerializer.Serialize(request)); process.StandardInput.Flush();
    }

    private GpuSpeechResponse Receive(TimeSpan timeout)
    {
        string line = process.StandardOutput.ReadLineAsync().WaitAsync(timeout).GetAwaiter().GetResult() ?? throw new IOException("GPU 识别已退出。");
        return JsonSerializer.Deserialize<GpuSpeechResponse>(line) ?? throw new IOException("GPU 识别没有返回结果。");
    }

    public VoiceTranscript Transcribe(string path)
    {
        Send(new(AudioPath: path));
        var response = Receive(TimeSpan.FromMinutes(30));
        if (!response.Ready || response.Transcript == null) throw new IOException("GPU 识别失败。");
        return response.Transcript;
    }

    public void Dispose()
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(3000)) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    // A native GPU crash stays in this worker; the export retries that recording on CPU.
    internal static void Run()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        void Reply(GpuSpeechResponse response) { Console.WriteLine(JsonSerializer.Serialize(response)); Console.Out.Flush(); }
        try
        {
            string? initial = Console.ReadLine();
            if (initial == null) return;
            var request = JsonSerializer.Deserialize<GpuSpeechRequest>(initial);
            if (request?.ModelPath == null) { Reply(new(false)); return; }
            int gpuLoaded = 0;
            using var logger = LogProvider.AddLogger((_, message) =>
            {
                // Confirm actual model offload, rather than a GPU-capable library silently using CPU.
                if (message != null && message.Contains("Vulkan", StringComparison.Ordinal) && message.Contains("total size", StringComparison.Ordinal))
                    Interlocked.Exchange(ref gpuLoaded, 1);
            });
            using var engine = new OfflineTranscriber(request.ModelPath, 1, useGpu: true);
            engine.WarmUp();
            if (Volatile.Read(ref gpuLoaded) == 0) { Reply(new(false)); return; }
            Reply(new(true));
            while (Console.ReadLine() is { } line)
            {
                var audio = JsonSerializer.Deserialize<GpuSpeechRequest>(line)?.AudioPath;
                if (audio == null) { Reply(new(false)); return; }
                var result = engine.Transcribe(audio);
                if (result.Status.StartsWith("离线识别组件", StringComparison.Ordinal)) { Reply(new(false)); return; }
                Reply(new(true, result));
            }
        }
        catch (Exception)
        {
            try { Reply(new(false)); }
            catch (IOException) { }
        }
    }
}
