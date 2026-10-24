using System.Security.Cryptography;
using System.Text.Json;

namespace WeChatNotes;

internal static class SpeechModel
{
    internal const long Length = 487601967;
    internal const string Hash = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";
    internal const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    private sealed record Location(string ModelPath);
    internal static string DefaultPathFor(string root) => Path.Combine(root, "models", "ggml-small.bin");
    internal static string PathFor(string root)
    {
        try
        {
            string file = Path.Combine(root, "speech-model.json");
            if (File.Exists(file) && new FileInfo(file).Length <= 16384 && JsonSerializer.Deserialize<Location>(File.ReadAllText(file)) is { ModelPath: { Length: > 0 } path } && Path.IsPathFullyQualified(path))
                return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) { }
        return DefaultPathFor(root);
    }
    internal static void SavePath(string root, string path)
    {
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择模型文件的完整路径。");
        Directory.CreateDirectory(root);
        string file = Path.Combine(root, "speech-model.json"), temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Location(Path.GetFullPath(path))));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static async Task ValidateAsync(string path, CancellationToken token, long length = Length, string hash = Hash)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (file.Length != length || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模型校验失败，请选择 Whisper Small 多语言模型 ggml-small.bin。");
    }
    internal static bool Exists(string root) => File.Exists(PathFor(root)) && new FileInfo(PathFor(root)).Length == Length;
    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal static async Task DownloadAsync(string root, IProgress<ExportProgress> progress, CancellationToken token)
    {
        using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromMinutes(30) };
        foreach (string url in new[] { Url, Url.Replace("huggingface.co", "hf-mirror.com") })
        {
            try
            {
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                connection.CancelAfter(TimeSpan.FromSeconds(25));
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, connection.Token);
                response.EnsureSuccessStatusCode(); connection.CancelAfter(Timeout.InfiniteTimeSpan);
                using var stream = await response.Content.ReadAsStreamAsync(token);
                await InstallAsync(stream, DefaultPathFor(root), Length, Hash, progress, token);
                SavePath(root, DefaultPathFor(root));
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && url == Url && ex is HttpRequestException or IOException or OperationCanceledException)
            { progress.Report(new("主站连接未完成，尝试模型镜像（仍校验官方 SHA256）")); }
        }
    }

    internal static async Task InstallAsync(Stream source, string path, long length, string hash, IProgress<ExportProgress> progress, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920]; int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    copied += count;
                    if (copied > length) throw new InvalidDataException("模型文件大小不符，请下载 Whisper Small 多语言模型。");
                    digest.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
                    progress.Report(new("下载 / 导入离线模型", (int)copied, (int)length));
                }
            }
            token.ThrowIfCancellationRequested();
            progress.Report(new("校验离线模型"));
            if (copied != length || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("模型校验失败，请重新下载或导入 ggml-small.bin。");
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
