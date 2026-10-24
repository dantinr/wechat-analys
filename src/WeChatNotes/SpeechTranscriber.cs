namespace WeChatNotes;

internal interface IGpuSpeechWorker : IDisposable
{
    VoiceTranscript Transcribe(string path);
}

internal sealed class SpeechTranscriber : IDisposable
{
    private readonly VoiceTranscriptionOptions options;
    private readonly OfflineTranscriber cpu;
    private readonly object gpuLock = new();
    private readonly Func<IGpuSpeechWorker> createGpu;
    private readonly Func<string, VoiceTranscript> transcribeCpu;
    private IGpuSpeechWorker? gpu;
    private bool gpuFailed;
    private string description;
    internal string Description => Volatile.Read(ref description);
    internal event Action<string>? StatusChanged;

    internal SpeechTranscriber(VoiceTranscriptionOptions options, Func<IGpuSpeechWorker>? createGpu = null, Func<string, VoiceTranscript>? transcribeCpu = null)
    {
        this.options = options;
        cpu = new(options.ModelPath, options.Parallelism);
        this.createGpu = createGpu ?? (() => new GpuSpeechWorker(options.ModelPath));
        this.transcribeCpu = transcribeCpu ?? cpu.Transcribe;
        description = options.Device == SpeechDevice.Cpu ? $"CPU · {options.Parallelism} 路" : "正在检测 GPU，首次加载请稍候";
    }

    private void SetDescription(string text)
    {
        Volatile.Write(ref description, text);
        StatusChanged?.Invoke(text);
    }

    internal VoiceTranscript Transcribe(string path)
    {
        if (options.Device != SpeechDevice.Cpu)
        {
            lock (gpuLock)
            {
                if (!gpuFailed)
                {
                    try
                    {
                        if (gpu == null)
                        {
                            gpu = createGpu();
                            SetDescription("GPU（Vulkan）· 单路");
                        }
                        return gpu.Transcribe(path) with { Model = "Whisper Small (offline, GPU Vulkan)" };
                    }
                    catch (Exception)
                    {
                        gpu?.Dispose(); gpu = null; gpuFailed = true;
                        SetDescription($"GPU 不可用，已回退 CPU · {options.Parallelism} 路");
                    }
                }
            }
        }
        return transcribeCpu(path) with { Model = "Whisper Small (offline, CPU)" };
    }

    public void Dispose() { gpu?.Dispose(); cpu.Dispose(); }
}
