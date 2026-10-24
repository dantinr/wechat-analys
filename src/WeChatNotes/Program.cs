namespace WeChatNotes;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Handle browser activation before the recorder mutex, including while the main window is running.
        if (args.Length > 0 && args[0] == "--locate-export")
        {
            try
            {
                if (args.Length != 2) throw new InvalidDataException("文件定位链接无效。");
                ExportFileLocator.Locate(args[1]);
            }
            catch (Exception ex) { MessageBox.Show("无法打开文件所在目录：" + ex.Message, "Ripper 文件定位", MessageBoxButtons.OK, MessageBoxIcon.Information); }
            return;
        }
        if (args.Length is 2 or 3 && args[0] == "--raw-probe")
        {
            RawProbe.Run(args[1], args.Length == 3 ? args[2] : null);
            return;
        }
        if (args.Length == 2 && args[0] == "--raw-test")
        {
            RawSelfTest.Run(args[1]);
            return;
        }
        if (args.Contains("--self-test"))
        {
            Application.Run(new SelfTestForm());
            return;
        }
        using var mutex = new Mutex(true, "Local\\WeChatNotes.LocalRecorder", out bool first);
        if (!first) { MessageBox.Show("程序已经在运行，请查看任务栏。", "Ripper"); return; }
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WeChatNotes");
        try
        {
            ExportFileLocator.Register();
            using var store = new Store(data);
            Application.Run(new MainForm(store));
        }
        catch (Exception ex)
        {
            MessageBox.Show("程序无法启动：" + ex.Message, "Ripper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
