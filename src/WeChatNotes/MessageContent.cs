using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WeChatNotes;

internal sealed record FileMessage(string Name, long? Length, string? Md5, bool ValidMetadata = true)
{
    internal bool SafeName => Name.Length is > 0 and <= 255 && Name is not "." and not ".." &&
        Name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !Name.EndsWith('.') && !Name.EndsWith(' ') &&
        !Regex.IsMatch(Name.Split('.')[0].TrimEnd(' '), @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}

internal sealed record ReadableMessage(string Text, FileMessage? File = null);

internal static class MessageContent
{
    internal static ReadableMessage Read(CaptureRecord record)
    {
        if (record.Message == null || (record.Message.RawType & 0xffffffff) == 1) return new(record.Text);
        long type = record.Message.RawType & 0xffffffff;
        string kind = RawChatSource.TypeName(type);
        string text = record.Text;
        int start = text.IndexOf('<');
        if (type is 49 or 10000 && start is >= 0 and < 200)
        {
            try
            {
                using var reader = XmlReader.Create(new StringReader(text[start..]), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                var app = doc.Root?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "appmsg");
                if (app != null && type == 49)
                {
                    string title = Value(app, "title"), appType = Value(app, "type");
                    if (appType == "6")
                    {
                        var attach = Child(app, "appattach");
                        string extension = Value(attach, "fileext").TrimStart('.');
                        string name = title;
                        if (Path.GetExtension(name).Length == 0 && extension.Length > 0) name += "." + extension;
                        string sizeText = Value(attach, "totallen"), checksum = Value(app, "md5");
                        if (checksum.Length == 0) checksum = Value(attach, "md5");
                        long? length = long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out long size) && size >= 0 ? size : null;
                        bool validHash = checksum.Length == 0 || Regex.IsMatch(checksum, "^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant);
                        var file = new FileMessage(name, length, checksum.Length > 0 && validHash ? checksum.ToLowerInvariant() : null,
                            validHash && (sizeText.Length == 0 || length.HasValue));
                        return new("文件：" + (name.Length == 0 ? "未提供文件名" : name), file);
                    }
                    if (appType == "57")
                    {
                        var reference = Child(app, "refermsg");
                        string quoted = Value(reference, "content"), sender = Value(reference, "displayname");
                        if (quoted.TrimStart().StartsWith('<')) quoted = "[消息]";
                        return new(title + (quoted.Length == 0 ? "" : $"\n引用{(sender.Length == 0 ? "" : " " + sender)}：{quoted}"));
                    }
                    string url = Value(app, "url");
                    return new((title.Length > 0 ? title : "[" + kind + "]") + (url.Length == 0 ? "" : "\n" + url));
                }
                string replacement = doc.Root?.DescendantsAndSelf().FirstOrDefault(e => e.Name.LocalName == "replacemsg")?.Value ?? "";
                if (replacement.Length > 0) return new(replacement);
            }
            catch (Exception ex) when (ex is XmlException or ArgumentException) { }
        }
        // Media bodies can contain transport XML; the readable transcript uses a short label.
        if (type is 3 or 34 or 43 or 47) return new("[" + kind + "]");
        return new(start is >= 0 and < 200 ? "[" + kind + "，正文请查看原始记录]" : text);
    }

    private static XElement? Child(XElement? element, string name) => element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    private static string Value(XElement? element, string name) => Child(element, name)?.Value ?? "";
}
