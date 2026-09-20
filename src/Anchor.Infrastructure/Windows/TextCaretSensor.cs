using System.Runtime.InteropServices;

namespace Anchor.Infrastructure.Windows;

/// <summary>
/// Whether the window in front is offering anywhere to type. Windows reports the caret of the
/// foreground thread, so an editor, a text box or a focused search field answers yes, while a
/// plain article, a video or a reader that takes no input answers no.
/// </summary>
public static class TextCaretSensor
{
    /// <summary>Null when the foreground thread could not be queried.</summary>
    public static bool? HasTextCaret()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        var thread = GetWindowThreadProcessId(window, out _);
        if (thread == 0)
        {
            return null;
        }

        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info))
        {
            return null;
        }

        return info.CaretWindow != IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }
}
