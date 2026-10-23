namespace WeChatNotes;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
            using var store = new Store(data);
            Application.Run(new MainForm(store));
        }
        catch (Exception ex)
        {
            MessageBox.Show("程序无法启动：" + ex.Message, "Ripper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
