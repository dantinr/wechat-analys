using System.Security.Cryptography;
using System.Text;

namespace WeChatNotes;

internal sealed record ExportedAttachment(string Name, string? RelativePath, string Status);
internal sealed record LocatedAttachment(string? FullPath, string Status);

internal sealed class AttachmentExporter(Store store, string folder)
{
    private sealed record LocalFile(string Path, long Length);
    private readonly Dictionary<string, string> sources = store.TargetSources();
    private readonly Dictionary<string, List<LocalFile>> indexes = new(StringComparer.OrdinalIgnoreCase);

    internal LocatedAttachment Locate(CaptureRecord record, FileMessage file)
    {
        var candidates = Candidates(record, file, out string? reason);
        if (reason != null) return new(null, reason);
        foreach (var candidate in candidates)
        {
            try
            {
                if ((File.GetAttributes(candidate.Path) & FileAttributes.ReparsePoint) != 0) continue;
                using var input = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (Matches(input, file)) return new(candidate.Path, "已找到本地文件。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return new(null, "未找到匹配的本地文件，可能尚未下载、已移动或不可读取。");
    }

    internal ExportedAttachment Copy(CaptureRecord record, FileMessage file)
    {
        ExportedAttachment Missing(string reason) => new(file.Name, null, reason);
        var candidates = Candidates(record, file, out string? reason);
        if (reason != null) return Missing(reason);
        bool matched = false;
        foreach (var candidate in candidates)
        {
            string? created = null;
            try
            {
                if ((File.GetAttributes(candidate.Path) & FileAttributes.ReparsePoint) != 0) continue;
                // Keep the same handle through verification and copying; writers/deleters cannot replace it.
                using var input = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!Matches(input, file)) continue;
                matched = true;
                input.Position = 0;
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.TargetId + "|" + record.Id))).ToLowerInvariant();
                string relative = Path.Combine("附件", key, file.Name);
                string directory = Path.Combine(folder, "附件");
                EnsureDirectory(directory); directory = Path.Combine(directory, key); EnsureDirectory(directory);
                string destination = Path.Combine(folder, relative);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { created = destination; input.CopyTo(output); }
                return new(file.Name, relative.Replace('\\', '/'), "已导出");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (created != null) try { File.Delete(created); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        return Missing(matched ? "已匹配本地文件，但无法写入导出目录，请检查文件占用或目录权限" : "未找到匹配的本地文件，可能尚未下载、已移动或不可读取");
    }

    private List<LocalFile> Candidates(CaptureRecord record, FileMessage file, out string? reason)
    {
        reason = null;
        if (!file.SafeName) { reason = "文件名无效"; return []; }
        if (!file.ValidMetadata || file.Md5 == null && !file.Length.HasValue)
        { reason = "缺少有效的文件校验资料"; return []; }
        bool knownSource = sources.TryGetValue(record.TargetId, out var sourceRoot);
        if (!knownSource && file.Md5 == null)
        { reason = "缺少附件来源目录及校验值，请通过“添加会话”选择原会话"; return []; }
        string[] roots = knownSource ? [sourceRoot!] : RawChatSource.Accounts();
        var candidates = roots.SelectMany(Index).Where(f => !file.Length.HasValue || f.Length == file.Length.Value)
            .DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => string.Equals(Path.GetFileName(f.Path), file.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (file.Md5 == null)
        {
            candidates = candidates.Where(f => string.Equals(Path.GetFileName(f.Path), file.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 1) { reason = "有多个同名文件，无法确认对应附件"; return []; }
        }
        return candidates;
    }

    private static bool Matches(FileStream input, FileMessage file) =>
        (!file.Length.HasValue || input.Length == file.Length.Value) &&
        (file.Md5 == null || Convert.ToHexString(MD5.HashData(input)).Equals(file.Md5, StringComparison.OrdinalIgnoreCase));

    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("附件目录不可用。");
        Directory.CreateDirectory(path);
    }

    private List<LocalFile> Index(string sourceRoot)
    {
        if (indexes.TryGetValue(sourceRoot, out var cached)) return cached;
        var files = new List<LocalFile>(); indexes[sourceRoot] = files;
        try
        {
            string? account = Path.GetDirectoryName(Path.GetFullPath(sourceRoot));
            if (account == null) return files;
            string messages = Path.Combine(account, "msg"), root = Path.Combine(messages, "file");
            if (!Directory.Exists(root) || new[] { account, messages, root }.Any(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)) return files;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                try { files.Add(new(path, new FileInfo(path).Length)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return files;
    }
}
