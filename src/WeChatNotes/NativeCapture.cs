using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace WeChatNotes;

public sealed record ChatWindow(nint Handle, string Title)
{
    public override string ToString() => Title;
}

public static class NativeWindows
{
    private delegate bool EnumWindowProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint data);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [StructLayout(LayoutKind.Sequential)] public struct NativeRect { public int Left, Top, Right, Bottom; }

    public static string Title(nint hwnd) { var text = new StringBuilder(1024); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    public static bool IsMain(string title) => title is "微信" or "Weixin" or "WeChat" or "";
    public static List<ChatWindow> Chats()
    {
        var result = new List<ChatWindow>();
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process) { try { if (process.ProcessName is "Weixin" or "WeChat") ids.Add(process.Id); } catch (InvalidOperationException) { } }
        }
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint id);
            if (ids.Contains((int)id) && IsWindowVisible(hwnd))
            {
                string title = Title(hwnd);
                if (!IsMain(title)) result.Add(new(hwnd, title));
            }
            return true;
        }, 0);
        return result;
    }
}

public sealed class WindowCapture : IDisposable
{
    private readonly IDirect3DDevice device;
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software,
        uint flags, nint levels, uint levelCount, uint sdkVersion, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device, out nint result);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindow(nint factory, nint hwnd, in Guid iid, out nint result);

    public WindowCapture()
    {
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("此电脑不支持窗口截图，需要 Windows 10 2004 或更新版本。");
        nint native = 0, context = 0, dxgi = 0, wrapped = 0;
        try
        {
            int hr = D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native, out _, out context);
            if (hr < 0) hr = D3D11CreateDevice(0, 5, 0, 0x20, 0, 0, 7, out native, out _, out context);
            Marshal.ThrowExceptionForHR(hr);
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out wrapped));
            device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(wrapped);
        }
        finally
        {
            foreach (nint pointer in new[] { wrapped, dxgi, context, native }) if (pointer != 0) Marshal.Release(pointer);
        }
    }

    private static GraphicsCaptureItem Item(nint hwnd)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint hstring = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out hstring));
            var iid = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in iid, out factory));
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindow>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 3 * nint.Size));
            var itemIid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(create(factory, hwnd, in itemIid, out item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    public async Task<Bitmap> CaptureAsync(nint hwnd, CancellationToken token = default)
    {
        if (!NativeWindows.IsWindow(hwnd)) throw new InvalidOperationException("聊天窗口已关闭，请重新打开并绑定。");
        if (NativeWindows.IsIconic(hwnd)) throw new InvalidOperationException("聊天窗口已最小化，已暂停此会话采集。请还原窗口。");
        var item = Item(hwnd);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        var completion = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (sender, _) =>
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame != null && !completion.TrySetResult(frame)) frame.Dispose();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        };
        session.StartCapture();
        Direct3D11CaptureFrame frame;
        try { frame = await completion.Task.WaitAsync(TimeSpan.FromSeconds(6), token); }
        catch
        {
            // A late frame still owns a GPU surface; dispose it even after timeout/cancellation.
            _ = completion.Task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); }, TaskScheduler.Default);
            throw;
        }
        using (frame)
        using (var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Ignore))
        {
            token.ThrowIfCancellationRequested();
            using var bgra = SoftwareBitmap.Convert(software, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            int width = Math.Min(frame.ContentSize.Width, bgra.PixelWidth), height = Math.Min(frame.ContentSize.Height, bgra.PixelHeight);
            var bytes = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyToBuffer(bytes.AsBuffer());
            var image = new Bitmap(bgra.PixelWidth, bgra.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, image.PixelFormat);
            try { Marshal.Copy(bytes, 0, data.Scan0, bytes.Length); }
            finally { image.UnlockBits(data); }
            if (width == image.Width && height == image.Height) return image;
            using (image) return image.Clone(new Rectangle(0, 0, width, height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        }
    }
    public void Dispose() => device.Dispose();
}
