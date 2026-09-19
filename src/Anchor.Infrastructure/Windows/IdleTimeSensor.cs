using System.Runtime.InteropServices;

namespace Anchor.Infrastructure.Windows;

public static class IdleTimeSensor
{
    public static double GetIdleSeconds()
    {
        var input = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref input))
        {
            return 0;
        }

        var elapsed = unchecked((long)(GetTickCount64() - input.Tick));
        return NormalizeMilliseconds(elapsed);
    }

    public static double NormalizeMilliseconds(long milliseconds) =>
        Math.Max(0, milliseconds) / 1_000d;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo input);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Tick;
    }
}
