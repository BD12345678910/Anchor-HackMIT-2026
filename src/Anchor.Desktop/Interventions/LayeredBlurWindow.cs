using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Anchor_Desktop.Interventions;

/// <summary>
/// A click-through, never-activated Win32 window painted with a per-pixel-alpha bitmap through
/// <c>UpdateLayeredWindow</c>. WinUI windows cannot host transparent regions, so the desktop image
/// blur draws its patches here and leaves everything between them fully see-through.
/// </summary>
internal sealed class LayeredBlurWindow : IDisposable
{
    private const string ClassName = "AnchorLayeredBlur";
    private static readonly WndProc KeepAliveProc = DefWindowProcW;
    private static ushort _classAtom;

    private readonly IntPtr _handle;
    private bool _visible;

    public LayeredBlurWindow()
    {
        var instance = GetModuleHandleW(null);
        if (_classAtom == 0)
        {
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                lpfnWndProc = KeepAliveProc,
                hInstance = instance,
                lpszClassName = ClassName,
            };
            _classAtom = RegisterClassExW(ref wc);
            if (_classAtom == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
        }

        _handle = CreateWindowExW(
            WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate,
            ClassName,
            "Anchor image blur",
            WsPopup,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    public IntPtr Handle => _handle;
    public bool IsVisible => _visible;

    /// <summary>Paints <paramref name="surface"/> (premultiplied ARGB) at the given screen origin.</summary>
    public void Present(Bitmap surface, Point origin)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var hBitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            hBitmap = CreateDibSection(surface, out var bits);
            CopyPixels(surface, bits);
            previous = SelectObject(memory, hBitmap);

            var size = new NativeSize { Width = surface.Width, Height = surface.Height };
            var destination = new NativePoint { X = origin.X, Y = origin.Y };
            var source = new NativePoint();
            var blend = new BlendFunction { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
            if (!UpdateLayeredWindow(_handle, screen, ref destination, ref size, memory, ref source, 0, ref blend, UlwAlpha))
            {
                throw new InvalidOperationException($"UpdateLayeredWindow failed with Win32 error {Marshal.GetLastWin32Error()}.");
            }
            if (!_visible)
            {
                ShowWindow(_handle, SwShowNoActivate);
                _visible = true;
            }
            SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public void Hide()
    {
        if (_visible)
        {
            ShowWindow(_handle, SwHide);
            _visible = false;
        }
    }

    public void Dispose()
    {
        Hide();
        DestroyWindow(_handle);
    }

    private static IntPtr CreateDibSection(Bitmap surface, out IntPtr bits)
    {
        var info = new BitmapInfo
        {
            Size = (uint)Marshal.SizeOf<BitmapInfo>(),
            Width = surface.Width,
            Height = -surface.Height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };
        var handle = CreateDIBSection(IntPtr.Zero, ref info, DibRgbColors, out bits, IntPtr.Zero, 0);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateDIBSection failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }
        return handle;
    }

    private static unsafe void CopyPixels(Bitmap surface, IntPtr bits)
    {
        var data = surface.LockBits(
            new Rectangle(0, 0, surface.Width, surface.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var rowBytes = surface.Width * 4;
            for (var y = 0; y < surface.Height; y++)
            {
                Buffer.MemoryCopy(
                    (byte*)data.Scan0 + y * data.Stride,
                    (byte*)bits + y * rowBytes,
                    rowBytes,
                    rowBytes);
            }
        }
        finally
        {
            surface.UnlockBits(data);
        }
    }

    private const uint WsPopup = 0x80000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const uint UlwAlpha = 0x00000002;
    private const byte AcSrcAlpha = 0x01;
    private const uint DibRgbColors = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint Color0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size,
        IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? module);
}
