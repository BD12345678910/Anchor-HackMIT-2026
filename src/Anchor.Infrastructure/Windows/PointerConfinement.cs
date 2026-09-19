using System.Runtime.InteropServices;

namespace Anchor.Infrastructure.Windows;

public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom);

public sealed class PointerConfinement
{
    public bool IsConfined { get; private set; }

    public void Confine(ScreenRect area)
    {
        if (area.Right <= area.Left || area.Bottom <= area.Top)
        {
            throw new ArgumentException("Confinement area must have positive dimensions.", nameof(area));
        }

        var native = new NativeRect(area.Left, area.Top, area.Right, area.Bottom);
        if (!ClipCursor(ref native))
        {
            throw new InvalidOperationException($"Pointer confinement failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        IsConfined = true;
    }

    public void Release()
    {
        ClipCursor(IntPtr.Zero);
        IsConfined = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);
}
