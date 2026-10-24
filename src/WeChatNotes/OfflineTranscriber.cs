using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Whisper.net;

namespace WeChatNotes;

public sealed record VoiceTranscriptionOptions(string ModelPath, int MaxParallelism = 4)
{
    internal int Parallelism => Math.Clamp(MaxParallelism, 1, 8);
}
public sealed record VoiceTranscript(string? Text, string Status, string Model = "Whisper Small (offline)");

internal sealed class OfflineTranscriber(string modelPath, int parallelism = 4) : IDisposable
{
    private readonly Lazy<WhisperFactory> factory = new(() => LoadFactory(modelPath));
    private readonly ConcurrentBag<WhisperProcessor> processors = new();
    private readonly object builderLock = new();
    private readonly SemaphoreSlim slots = new(Math.Clamp(parallelism, 1, 8));
    private int createdProcessors;
    internal int CreatedProcessors => createdProcessors;

    private static WhisperFactory LoadFactory(string path)
    {
        if (!SpeechModel.Supported) throw new PlatformNotSupportedException();
        using (var file = File.OpenRead(path))
            if (file.Length != SpeechModel.Length || !Convert.ToHexString(SHA256.HashData(file)).Equals(SpeechModel.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模型校验失败");
        return WhisperFactory.FromPath(path);
    }

    internal VoiceTranscript Transcribe(string path)
    {
        slots.Wait();
        WhisperProcessor? processor = null;
        float[]? samples = null;
        try
        {
            samples = ReadSamples(path);
            if (!HasSpeechEnergy(samples)) return new(null, "未检测到可识别语音");
            if (!processors.TryTake(out processor))
            {
                // One model, separate processors and native inference states for concurrent recordings.
                // Keep string pooling disabled: Whisper's optional shared pool is not thread safe.
                lock (builderLock)
                {
                    processor = factory.Value.CreateBuilder().WithLanguage("auto").WithNoContext()
                        .WithPrompt("以下是微信聊天语音，请使用简体中文记录中文内容，保留原文语言。")
                        .WithThreads(Math.Clamp(Environment.ProcessorCount / Math.Clamp(parallelism, 1, 8), 1, 8)).Build();
                    Interlocked.Increment(ref createdProcessors);
                }
            }
            var text = Collect(processor, samples).GetAwaiter().GetResult();
            return text.Length == 0 ? new(null, "未识别到文字，请收听原音核对") : new(text, "已离线转写，识别内容请结合原音核对");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { return new(null, "音频格式不支持、文件不可读或模型校验失败，请检查模型并收听原音"); }
        catch (Exception)
        {
            return new(null, "离线识别组件不可用或识别失败，请检查模型、Windows 11 和 VC++ 2022 x64 运行库");
        }
        finally
        {
            if (samples != null) CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(samples.AsSpan()));
            if (processor != null) processors.Add(processor);
            slots.Release();
        }
    }

    private static async Task<string> Collect(WhisperProcessor processor, float[] samples)
    {
        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples).ConfigureAwait(false))
            if (segment.NoSpeechProbability < .6f) text.Append(segment.Text);
        return text.ToString().Trim();
    }

    internal static float[] ReadSamples(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > SilkAudio.MaximumPcmBytes + 4096 || input.Length < 44) throw new InvalidDataException();
        using var reader = new BinaryReader(input);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException();
        reader.ReadUInt32(); if (reader.ReadUInt32() != 0x45564157) throw new InvalidDataException();
        int rate = 0, channels = 0, bits = 0, format = 0;
        byte[]? pcm = null;
        try
        {
            while (input.Length - input.Position >= 8)
            {
                uint chunk = reader.ReadUInt32(), length = reader.ReadUInt32();
                if (length > input.Length - input.Position) throw new InvalidDataException();
                long next = input.Position + length + (length & 1);
                if (chunk == 0x20746d66 && length >= 16)
                { format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32(); reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16(); }
                else if (chunk == 0x61746164 && pcm == null && length <= SilkAudio.MaximumPcmBytes) pcm = reader.ReadBytes((int)length);
                input.Position = next;
            }
            if (format != 1 || channels is < 1 or > 2 || bits != 16 || rate is < 8000 or > 48000 || pcm == null || pcm.Length == 0 || pcm.Length % (channels * 2) != 0) throw new InvalidDataException();
            int frames = pcm.Length / (channels * 2);
            int count = checked((int)((long)frames * 16000 / rate));
            if (count == 0 || count > 16000 * 600) throw new InvalidDataException();
            var result = new float[count];
            float Sample(int frame)
            {
                float value = 0;
                for (int c = 0; c < channels; c++) value += BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan((frame * channels + c) * 2)) / 32768f;
                return value / channels;
            }
            for (int i = 0; i < count; i++)
            {
                double position = (double)i * rate / 16000; int left = Math.Min((int)position, frames - 1), right = Math.Min(left + 1, frames - 1);
                result[i] = Sample(left) + (Sample(right) - Sample(left)) * (float)(position - left);
            }
            return result;
        }
        finally { if (pcm != null) CryptographicOperations.ZeroMemory(pcm); }
    }

    internal static bool HasSpeechEnergy(float[] samples) => samples.Length >= 1600 && samples.Any(s => Math.Abs(s) > .003f);
    // The export joins all workers before disposing the pool and its shared model.
    public void Dispose()
    {
        while (processors.TryTake(out var processor)) processor.Dispose();
        if (factory.IsValueCreated) factory.Value.Dispose();
        slots.Dispose();
    }
}
