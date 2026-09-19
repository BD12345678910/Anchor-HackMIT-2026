using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Anchor_Desktop.Overlays;

internal static class OverlayWindowHelper
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint MonitorDefaultToNearest = 0x00000002;

    public static void Configure(Window window, int width, int height, bool clickThrough = false, bool fullScreen = false)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        var workArea = GetActiveWorkArea(handle);
        var bounds = fullScreen
            ? workArea
            : new RectInt32(
                Math.Max(workArea.X + 16, workArea.X + workArea.Width - width - 24),
                workArea.Y + 24,
                width,
                height);
        ConfigureBounds(window, bounds, clickThrough);
    }

    public static void ConfigureBounds(Window window, RectInt32 bounds, bool clickThrough)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        ConfigurePresenter(appWindow, hideBorder: true);
        appWindow.MoveAndResize(bounds);

        if (clickThrough)
        {
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        }
    }

    public static void Center(Window window, int width, int height)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(id);
        ConfigurePresenter(appWindow, hideBorder: false);

        var workArea = GetActiveWorkArea(handle);
        appWindow.MoveAndResize(new RectInt32(
            workArea.X + Math.Max(0, (workArea.Width - width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - height) / 2),
            width,
            height));
    }

    internal static RectInt32 GetActiveWorkArea(IntPtr fallbackWindow)
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(
            foreground != IntPtr.Zero ? foreground : fallbackWindow,
            MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new RectInt32(
                info.Work.Left,
                info.Work.Top,
                info.Work.Right - info.Work.Left,
                info.Work.Bottom - info.Work.Top);
        }

        return new RectInt32(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
    }

    private static void ConfigurePresenter(AppWindow appWindow, bool hideBorder)
    {
        if (appWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        if (hideBorder)
        {
            presenter.SetBorderAndTitleBar(false, false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
