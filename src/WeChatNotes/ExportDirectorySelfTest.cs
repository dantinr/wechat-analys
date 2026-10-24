namespace WeChatNotes;

internal static class ExportDirectorySelfTest
{
    internal static void Run(string root, Action<string, bool> check)
    {
        string parent = Path.Combine(root, "会话导出目录");
        string name = "产品讨论群 [一组] 🙂";
        string first = ExportDirectory.Create(parent, name);
        check("导出目录直接使用聊天窗口名称并保留中文空格和表情", Path.GetFileName(first) == name && Directory.Exists(first));
        File.WriteAllText(Path.Combine(first, "保留.txt"), "首次导出内容");
        string second = ExportDirectory.Create(parent, name);
        File.WriteAllText(Path.Combine(parent, name + " (3)"), "已有同名文件");
        string fourth = ExportDirectory.Create(parent, name);
        check("重复导出自动编号且不覆盖已有目录和同名文件", Path.GetFileName(second) == name + " (2)" && Path.GetFileName(fourth) == name + " (4)" && File.ReadAllText(Path.Combine(first, "保留.txt")) == "首次导出内容" && File.ReadAllText(Path.Combine(parent, name + " (3)")) == "已有同名文件" && !Directory.GetDirectories(parent, ".Ripper_export_*").Any());
        string[] unusual = ["A/B\\C:项目?*\"<>|\n", "..", " ", "CON", "nul.txt", "LPT¹.log", "结尾. ", "C:\\绝对目录"];
        var paths = unusual.Select(value => ExportDirectory.Create(parent, value)).ToArray();
        check("非法字符保留名空名称和路径内容转换为父目录内的有效目录", paths.All(path => Directory.Exists(path) && Path.GetDirectoryName(path) == parent) && Path.GetFileName(paths[0]).StartsWith("A_B_C_项目", StringComparison.Ordinal) && Path.GetFileName(paths[1]) == "聊天记录" && Path.GetFileName(paths[3]) == "_CON");
        string longName = new string('群', 79) + "🙂聊天";
        string longPath = ExportDirectory.Create(parent, longName);
        string segment = Path.GetFileName(longPath);
        check("超长聊天名称截短时不拆分表情代理对且仍能创建目录", segment.Length <= 80 && !char.IsHighSurrogate(segment[^1]) && Directory.Exists(longPath));
    }
}
