using System.Text.RegularExpressions;

namespace WeChatNotes;

internal static class ExportDirectory
{
    internal static string NameFor(string conversation)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string name = new(conversation.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
        name = name.Trim().TrimEnd('.', ' ');
        if (name.Length > 80)
        {
            int end = char.IsHighSurrogate(name[79]) ? 79 : 80;
            name = name[..end].TrimEnd('.', ' ');
        }
        if (name.Length == 0) return "聊天记录";
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) name = "_" + name;
        return name;
    }

    internal static string Create(string parent, string conversation)
    {
        string name = NameFor(conversation);
        string staging = Path.Combine(Path.GetFullPath(parent), ".Ripper_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            for (int index = 1; ; index++)
            {
                string destination = Path.Combine(Path.GetFullPath(parent), index == 1 ? name : $"{name} ({index})");
                // Renaming an empty staging directory reserves the name without reusing an existing export.
                try { Directory.Move(staging, destination); return destination; }
                catch (IOException) when (Directory.Exists(destination) || File.Exists(destination)) { }
            }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging);
        }
    }
}
