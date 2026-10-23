using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal sealed record ExportedVoice(string Name, string? RelativePath, string Status, string? OriginalPath = null);

internal sealed class VoiceExporter(Store store, string folder,
    Func<string, IReadOnlyList<CaptureRecord>, Dictionary<string, VoicePayload>>? read = null)
{
    internal Dictionary<string, ExportedVoice> Copy(IReadOnlyList<CaptureRecord> captures)
    {
        var result = new Dictionary<string, ExportedVoice>(StringComparer.Ordinal);
        var sources = store.TargetSources();
        var voices = captures.Where(c => c.Message != null && (c.Message.RawType & 0xffffffff) == 34).ToList();
        foreach (var record in voices.Where(c => !sources.ContainsKey(c.TargetId)))
            result[record.Id] = new("", null, "缺少语音来源目录，请重新绑定原会话后导出");
        foreach (var group in voices.Where(c => sources.ContainsKey(c.TargetId)).GroupBy(c => sources[c.TargetId], StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, VoicePayload>? payloads = null;
            try
            {
                payloads = (read ?? RawVoiceSource.Read)(group.Key, group.ToList());
                foreach (var record in group)
                {
                    result[record.Id] = payloads.TryGetValue(record.Id, out var payload)
                        ? Save(record, payload) : new("", null, "未找到对应的本地音频");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CryptographicException or SqliteException or ArgumentException)
            {
                foreach (var record in group)
                    result[record.Id] = new("", null, "无法读取本地语音库，请保持微信登录、加载语音后重新导出");
            }
            finally
            {
                if (payloads != null) foreach (var payload in payloads.Values) RawVoiceSource.Clear(payload);
            }
        }
        return result;
    }

    private ExportedVoice Save(CaptureRecord record, VoicePayload payload)
    {
        if (payload.Bytes is not { Length: > 0 } bytes) return new("", null, payload.Status);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.TargetId + "|" + record.Id))).ToLowerInvariant();
        string name = "语音_" + record.Message!.SentAt.ToOffset(TimeSpan.FromHours(8)).ToString("yyyyMMdd_HHmmss") + Extension(bytes);
        string relative = Path.Combine("语音", key, name);
        try
        {
            string directory = Path.Combine(folder, "语音"); EnsureDirectory(directory);
            directory = Path.Combine(directory, key); EnsureDirectory(directory);
            string destination = Path.Combine(folder, relative);
            WriteNew(destination, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(name, null, "无法写入语音文件，请检查文件占用或导出目录权限");
        }
        string original = relative.Replace('\\', '/');
        if (Extension(bytes) != ".silk") return new(name, original, "已导出原始音频");
        byte[]? wave = null;
        try
        {
            wave = SilkAudio.ToWave(bytes);
            string waveRelative = Path.ChangeExtension(relative, ".wav");
            WriteNew(Path.Combine(folder, waveRelative), wave);
            return new(Path.ChangeExtension(name, ".wav"), waveRelative.Replace('\\', '/'), "已转换为 WAV，原始 SILK 已保留", original);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(name, original, "WAV 转换失败，已保留原始 SILK");
        }
        finally { if (wave != null) CryptographicOperations.ZeroMemory(wave); }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        bool created = false;
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true; output.Write(bytes);
        }
        catch
        {
            if (created) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string Extension(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("#!SILK_V3"u8) || bytes.Length > 1 && bytes[0] == 2 && bytes[1..].StartsWith("#!SILK_V3"u8)) return ".silk";
        if (bytes.StartsWith("#!AMR"u8)) return ".amr";
        if (bytes.StartsWith("RIFF"u8) && bytes.Length >= 12 && bytes[8..].StartsWith("WAVE"u8)) return ".wav";
        if (bytes.StartsWith("OggS"u8)) return ".ogg";
        return ".bin";
    }

    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("语音目录不可用。");
        Directory.CreateDirectory(path);
    }
}
