using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WeChatNotes;

internal static class SilkAudio
{
    internal const int SampleRate = 24000;
    internal const int MaximumPcmBytes = 32 * 1024 * 1024;
    private const int MaximumSamplesPerPacket = SampleRate / 50 * 5;
    private static readonly Lazy<Decode> Decoder = new(Load);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Decode([In] byte[] input, int length, [Out] short[] output, int capacity, out int written);

    internal static byte[] ToWave(byte[] silk)
    {
        int packets = Validate(silk);
        short[] samples = new short[checked(packets * MaximumSamplesPerPacket)];
        try
        {
            if (Decoder.Value(silk, silk.Length, samples, samples.Length, out int written) != 0 || written <= 0 || written > samples.Length)
                throw new InvalidDataException("SILK 数据无法完整解码。");
            byte[] wave = new byte[checked(44 + written * 2)];
            "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
            "WAVEfmt "u8.CopyTo(wave.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1); // PCM
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1); // Mono
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), SampleRate * 2);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
            "data"u8.CopyTo(wave.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), written * 2);
            for (int i = 0; i < written; i++) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), samples[i]);
            return wave;
        }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(samples.AsSpan())); }
    }

    private static int Validate(ReadOnlySpan<byte> silk)
    {
        if (silk.Length > RawVoiceSource.MaximumBytes) throw new InvalidDataException("SILK 文件过大。");
        int offset = silk.Length > 0 && silk[0] == 2 ? 1 : 0;
        if (!silk[offset..].StartsWith("#!SILK_V3"u8)) throw new InvalidDataException("SILK 文件头无效。");
        offset += 9;
        int packets = 0;
        while (offset < silk.Length)
        {
            if (silk.Length - offset < 2) throw new InvalidDataException("SILK 帧长度不完整。");
            short length = BinaryPrimitives.ReadInt16LittleEndian(silk[offset..]); offset += 2;
            if (length == -1 && offset == silk.Length) break;
            if (length < 0 || length > 1024 || length > silk.Length - offset) throw new InvalidDataException("SILK 帧内容不完整或过大。");
            if (++packets > MaximumPcmBytes / 2 / MaximumSamplesPerPacket) throw new InvalidDataException("SILK 转换超过单条音频大小限制。");
            offset += length;
        }
        if (packets == 0) throw new InvalidDataException("SILK 文件不包含音频帧。");
        return packets;
    }

    private static Decode Load()
    {
        using var resource = typeof(SilkAudio).Assembly.GetManifestResourceStream("WeChatNotes.Native.Silk.dll")
            ?? throw new DllNotFoundException("缺少随程序提供的 SILK 解码组件。");
        using var buffer = new MemoryStream(); resource.CopyTo(buffer);
        byte[] bytes = buffer.ToArray(), hash = SHA256.HashData(bytes);
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatNotes", "codecs", Convert.ToHexString(hash).ToLowerInvariant());
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("解码组件目录不可用。");
        string path = Path.Combine(directory, "WeChatNotesSilk.dll");
        if (!File.Exists(path))
        {
            string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("解码组件文件不可用。");
        // Hold this exact file against replacement through hash verification and loading.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), hash)) throw new IOException("解码组件校验失败。");
        nint library = NativeLibrary.Load(path);
        return Marshal.GetDelegateForFunctionPointer<Decode>(NativeLibrary.GetExport(library, "wn_silk_decode"));
    }
}
