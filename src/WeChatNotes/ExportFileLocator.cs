using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WeChatNotes;

internal sealed record ExportFileIndex(string Id, string[] Files);

internal static class ExportFileLocator
{
    internal const string Scheme = "ripper-locate";
    internal const string IndexName = "ripper-files.json";

    internal static ExportFileIndex WriteIndex(string folder, IEnumerable<string?> files)
    {
        var index = new ExportFileIndex(Guid.NewGuid().ToString("N"), files.OfType<string>().Distinct(StringComparer.Ordinal).ToArray());
        string path = Path.Combine(folder, IndexName);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("文件定位索引不可写入。");
        File.WriteAllText(path, JsonSerializer.Serialize(index));
        return index;
    }

    internal static bool Register()
    {
        try
        {
            string executable = Environment.ProcessPath ?? throw new IOException();
            string? assembly = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(AppContext.BaseDirectory, "Ripper.dll") : null;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            WriteRegistration(key, executable, assembly);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    internal static void WriteRegistration(RegistryKey key, string executable, string? assembly = null)
    {
        key.SetValue("", "URL:Ripper 文件定位"); key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{executable}\" " + (assembly == null ? "" : $"\"{assembly}\" ") + "--locate-export \"%1\"");
    }

    internal static string Resolve(string request)
    {
        const string unavailable = "无法定位该文件。请保留完整导出目录，确认文件仍然存在；旧版导出请用新版 Ripper 重新导出。";
        if (request.Length > 65536 || !Uri.TryCreate(request, UriKind.Absolute, out var uri) || uri.Scheme != Scheme || uri.Host != "file"
            || uri.UserInfo.Length != 0 || uri.Port != -1 || uri.Fragment.Length != 0 || !Guid.TryParseExact(uri.AbsolutePath.Trim('/'), "N", out var id)
            || !uri.Query.StartsWith("?path=", StringComparison.Ordinal) || uri.Query.Contains('&')) throw new InvalidDataException(unavailable);
        string fileUrl = Uri.UnescapeDataString(uri.Query[6..]);
        if (!Uri.TryCreate(fileUrl, UriKind.Absolute, out var file) || !file.IsFile || file.IsUnc || file.Host.Length != 0
            || file.Query.Length != 0 || file.Fragment.Length != 0) throw new InvalidDataException(unavailable);
        string path = Path.GetFullPath(file.LocalPath);
        // Only local exported attachments/audio can be selected; never execute a file or accept a network path.
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path[2..].Contains(':')
            || new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network || !File.Exists(path))
            throw new InvalidDataException(unavailable);
        string? root = new FileInfo(path).Directory?.Parent?.Parent?.FullName;
        if (root == null) throw new InvalidDataException(unavailable);
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        string[] parts = relative.Split('/');
        if (parts.Length != 3 || parts[0] is not ("附件" or "语音") || parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit))
            throw new InvalidDataException(unavailable);
        for (FileSystemInfo? item = new FileInfo(path); item != null; item = item is FileInfo info ? info.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(unavailable);
        string indexPath = Path.Combine(root, IndexName);
        var indexFile = new FileInfo(indexPath);
        if (!indexFile.Exists || indexFile.Length > 64 * 1024 * 1024 || (indexFile.Attributes & FileAttributes.ReparsePoint) != 0
            || !File.Exists(Path.Combine(root, "聊天记录.html"))) throw new InvalidDataException(unavailable);
        ExportFileIndex? index;
        try { index = JsonSerializer.Deserialize<ExportFileIndex>(File.ReadAllText(indexPath)); }
        catch (JsonException ex) { throw new InvalidDataException(unavailable, ex); }
        if (index?.Id != id.ToString("N") || index.Files == null || !index.Files.Contains(relative, StringComparer.Ordinal))
            throw new InvalidDataException(unavailable);
        return path;
    }

    internal static void Locate(string request, Action<string>? reveal = null) => (reveal ?? Reveal)(Resolve(request));

    internal static IntPtr ParseShellItem(string path)
    {
        Application.OleRequired();
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var item, 0, out _));
        return item;
    }

    private static void Reveal(string path)
    {
        var item = ParseShellItem(path);
        try { Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, IntPtr.Zero, 0)); }
        finally { Marshal.FreeCoTaskMem(item); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr item, uint attributes, out uint result);
    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr item, uint count, IntPtr children, uint flags);
}
