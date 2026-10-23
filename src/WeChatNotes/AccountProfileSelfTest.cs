using Microsoft.Data.Sqlite;

namespace WeChatNotes;

internal static class AccountProfileSelfTest
{
    internal static void Run(string root, string report, Action<string, bool> check)
    {
        using var contacts = new SqliteConnection("Data Source=:memory:"); contacts.Open();
        using var images = new SqliteConnection("Data Source=:memory:"); images.Open();
        using (var command = contacts.CreateCommand())
        {
            command.CommandText = "CREATE TABLE contact(username TEXT,nick_name TEXT); INSERT INTO contact VALUES('wxid_synthetic','合成微信用户'),('other','其他联系人'),('custom_account','合成自定义账号')";
            command.ExecuteNonQuery();
        }
        using (var command = images.CreateCommand())
        {
            command.CommandText = "CREATE TABLE head_image(username TEXT,image_buffer BLOB,update_time INTEGER)"; command.ExecuteNonQuery();
        }
        byte[] Png(Color color, int width = 32)
        {
            using var bitmap = new Bitmap(width, 32); using var graphics = Graphics.FromImage(bitmap); graphics.Clear(color);
            using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
        }
        void ImageRow(string id, byte[] bytes, int timestamp)
        {
            using var command = images.CreateCommand(); command.CommandText = "INSERT INTO head_image VALUES($id,$bytes,$time)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$bytes", bytes); command.Parameters.AddWithValue("$time", timestamp); command.ExecuteNonQuery();
        }
        byte[] red = Png(Color.Red), blue = Png(Color.Blue);
        ImageRow("wxid_synthetic", red, 1); ImageRow("wxid_synthetic", blue, 2); ImageRow("other", red, 99);
        WeChatProfile? Read(string folder) => WeChatProfiles.Read(contacts, images, folder, CancellationToken.None);
        var profile = Read("wxid_synthetic_123abc");
        check("头像按账号目录精确匹配本人的昵称和最新缓存", profile?.Name == "合成微信用户" && profile.Avatar!.SequenceEqual(blue));
        check("自定义账号名中的下划线不会被错误截断", Read("custom_account") is { Name: "合成自定义账号", Avatar: null });
        check("未识别本人时不使用其他联系人的头像", Read("missing_123abc") == null);
        using (var command = contacts.CreateCommand())
        { command.CommandText = "INSERT INTO contact VALUES('wxid_synthetic_123abc','有歧义的合成用户')"; command.ExecuteNonQuery(); }
        check("账号身份存在歧义时不猜测头像归属", Read("wxid_synthetic_123abc") == null);
        ImageRow("custom_account", new byte[WeChatProfiles.MaximumAvatarBytes + 1], 1);
        check("头像缺失或缓存过大时仍可显示账号昵称", Read("custom_account") is { Name: "合成自定义账号", Avatar: null });
        using (var decoded = AccountProfileView.DecodeAvatar(blue))
            check("头像脱离原始流后可独立绘制", decoded is { Width: 32, Height: 32 } && decoded.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb());
        check("损坏或尺寸异常的头像回退默认图标", AccountProfileView.DecodeAvatar([1, 2, 3]) == null && AccountProfileView.DecodeAvatar(Png(Color.Red, 2049)) == null);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool canceled = false;
            try { WeChatProfiles.Read(contacts, images, "wxid_synthetic", cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            check("头像读取支持取消且失效目录不会触发微信连接", canceled && WeChatProfiles.Read(Path.Combine(root, "no-avatar-account"), CancellationToken.None) == null);
        }

        using var delayed = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var host = new Form { Width = 520, Height = 180, Opacity = 0, ShowInTaskbar = false };
        using var view = new AccountProfileView((account, _) =>
        {
            if (account == "old") { started.Set(); delayed.Wait(TimeSpan.FromSeconds(5)); return new("旧合成账号", red); }
            if (account == "broken") throw new IOException("合成头像读取失败");
            return new("当前合成账号", blue);
        }) { Dock = DockStyle.Right };
        host.Controls.Add(view); host.Show();
        void Wait(Task task)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!task.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            if (!task.IsCompleted) throw new TimeoutException("合成头像任务未完成");
            task.GetAwaiter().GetResult();
        }
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        var avatar = Descendants(view).OfType<PictureBox>().Single();
        Wait(view.ShowAccountAsync("new"));
        check("主界面头像区域显示账号昵称和解码头像", Descendants(view).OfType<Label>().Any(l => l.Text == "当前合成账号") && avatar.AccessibleDescription == "当前微信账号的本地头像");
        Task old = view.ShowAccountAsync("old");
        if (!started.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("合成头像后台读取未启动");
        Wait(view.ShowAccountAsync("new")); delayed.Set(); Wait(old);
        check("快速切换账号时旧结果不会覆盖当前头像", Descendants(view).OfType<Label>().Any(l => l.Text == "当前合成账号") && ((Bitmap)avatar.Image!).GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb());
        using (var bitmap = new Bitmap(host.Width, host.Height))
        {
            host.DrawToBitmap(bitmap, new(Point.Empty, host.Size));
            bitmap.Save(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + "-account-profile.png"));
        }
        Wait(view.ShowAccountAsync("broken"));
        check("微信退出或头像读取失败时回退占位且不阻塞界面", avatar.AccessibleDescription == "默认头像" && Descendants(view).OfType<Label>().Any(l => l.Text == "暂未读取到头像"));
        Wait(view.ShowAccountAsync(null));
        check("没有会话时清除上一个账号的头像和昵称", avatar.AccessibleDescription == "默认头像" && Descendants(view).OfType<Label>().Any(l => l.Text == "未选择微信账号"));
        host.Close();
    }
}
