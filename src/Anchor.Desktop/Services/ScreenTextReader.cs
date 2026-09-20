using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Anchor.Core.Models;
using Anchor.Core.Services;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Anchor_Desktop.Services;

/// <summary>
/// Reads the text visible in the front window entirely on-device: the window is captured with
/// <c>PrintWindow</c> and recognised with the Windows OCR engine (<c>Windows.Media.Ocr</c>).
/// The result is the input for screen-based progress detection and for the "last line you were
/// on" reminder, anchored to gaze (camera), the text caret, or the pointer, in that order.
/// </summary>
public sealed class ScreenTextReader
{
    private const uint PwRenderFullContent = 0x00000002;
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly OcrEngine? _engine;

    public ScreenTextReader()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            Status = _engine is null
                ? "Windows OCR is not available on this device"
                : $"Windows OCR ready ({_engine.RecognizerLanguage.DisplayName})";
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or TypeLoadException)
        {
            _engine = null;
            Status = $"Windows OCR unavailable: {error.Message}";
        }
    }

    public bool IsAvailable => _engine is not null;
    public string Status { get; private set; }

    /// <summary>
    /// Captures and recognises the front window. Returns <c>null</c> when nothing safe is in front
    /// (Anchor itself, the shell, a secure window, a minimised window) or when capture fails.
    /// </summary>
    public async Task<ScreenSnapshot?> ReadForegroundAsync(
        GazeSample? gaze,
        bool isSecureWindow,
        CancellationToken cancellationToken = default)
    {
        if (_engine is null || isSecureWindow)
        {
            return null;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsIconic(foreground) || !GetWindowRect(foreground, out var native))
        {
            return null;
        }
        var threadId = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == _ownProcessId || ShellClasses.Contains(GetClassName(foreground), StringComparer.Ordinal))
        {
            return null;
        }
        var bounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        if (bounds.Width < 64 || bounds.Height < 64)
        {
            return null;
        }

        var processName = ProcessNameOf(processId);
        var title = GetWindowTitle(foreground);
        var gazePoint = ToWindowLocal(gaze, bounds);
        var caretPoint = CaretPoint(threadId, bounds);
        var pointerPoint = PointerPoint(bounds);

        var stopwatch = Stopwatch.StartNew();
        byte[] pixels;
        int width;
        int height;
        double scale;
        try
        {
            (pixels, width, height, scale) = await Task.Run(() => CapturePixels(foreground, bounds), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (pixels.Length == 0)
        {
            Status = $"Watching {processName} · window cannot be captured";
            return null;
        }

        OcrResult result;
        using (var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied))
        {
            try
            {
                result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
            }
            catch (Exception error) when (error is COMException or ArgumentException)
            {
                Status = $"OCR failed on {processName}: {error.Message}";
                return null;
            }
        }

        var lines = new List<ScreenLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var text = line.Text?.Trim();
            if (string.IsNullOrEmpty(text) || line.Words.Count == 0)
            {
                continue;
            }
            var left = line.Words.Min(static word => word.BoundingRect.Left);
            var top = line.Words.Min(static word => word.BoundingRect.Top);
            var right = line.Words.Max(static word => word.BoundingRect.Right);
            var bottom = line.Words.Max(static word => word.BoundingRect.Bottom);
            lines.Add(new ScreenLine(text, left / scale, top / scale, (right - left) / scale, (bottom - top) / scale));
        }

        var snapshot = ScreenSnapshotAnalyzer.Build(
            DateTimeOffset.UtcNow,
            processName,
            title,
            lines,
            gazePoint,
            caretPoint,
            pointerPoint,
            bounds.Width,
            bounds.Height);
        Status = $"Read {lines.Count} lines from {processName} in {stopwatch.ElapsedMilliseconds} ms · anchor: {snapshot.FocusSource}";
        return snapshot;
    }

    private static (byte[] Pixels, int Width, int Height, double Scale) CapturePixels(IntPtr foreground, Rectangle bounds)
    {
        using var capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        bool captured;
        using (var graphics = Graphics.FromImage(capture))
        {
            var dc = graphics.GetHdc();
            try
            {
                captured = PrintWindow(foreground, dc, PwRenderFullContent);
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }
        if (!captured)
        {
            return ([], 0, 0, 1);
        }

        var maxDimension = (int)OcrEngine.MaxImageDimension;
        var scale = 1.0;
        Bitmap source = capture;
        Bitmap? scaled = null;
        try
        {
            if (capture.Width > maxDimension || capture.Height > maxDimension)
            {
                scale = Math.Min((double)maxDimension / capture.Width, (double)maxDimension / capture.Height);
                scaled = new Bitmap(
                    Math.Max(1, (int)(capture.Width * scale)),
                    Math.Max(1, (int)(capture.Height * scale)),
                    PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(scaled);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(capture, new Rectangle(Point.Empty, scaled.Size));
                source = scaled;
            }

            var data = source.LockBits(
                new Rectangle(Point.Empty, source.Size),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                if (data.Stride == source.Width * 4)
                {
                    return (pixels, source.Width, source.Height, scale);
                }

                var packed = new byte[source.Width * 4 * source.Height];
                for (var row = 0; row < source.Height; row++)
                {
                    Buffer.BlockCopy(pixels, row * data.Stride, packed, row * source.Width * 4, source.Width * 4);
                }
                return (packed, source.Width, source.Height, scale);
            }
            finally
            {
                source.UnlockBits(data);
            }
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private static (double X, double Y)? ToWindowLocal(GazeSample? gaze, Rectangle bounds)
    {
        if (gaze is not { Available: true } || gaze.X is null || gaze.Y is null)
        {
            return null;
        }
        var x = gaze.X.Value * Math.Max(1, GetSystemMetrics(0)) - bounds.Left;
        var y = gaze.Y.Value * Math.Max(1, GetSystemMetrics(1)) - bounds.Top;
        return x < 0 || y < 0 || x > bounds.Width || y > bounds.Height ? null : (x, y);
    }

    private static (double X, double Y)? CaretPoint(uint threadId, Rectangle bounds)
    {
        var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndCaret == IntPtr.Zero)
        {
            return null;
        }
        var point = new NativePoint { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        if (!ClientToScreen(info.hwndCaret, ref point))
        {
            return null;
        }
        var x = point.X - bounds.Left;
        var y = point.Y - bounds.Top + (info.rcCaret.Bottom - info.rcCaret.Top) / 2.0;
        return x < 0 || y < 0 || x > bounds.Width || y > bounds.Height ? null : (x, y);
    }

    private static (double X, double Y)? PointerPoint(Rectangle bounds)
    {
        if (!GetCursorPos(out var point))
        {
            return null;
        }
        var x = point.X - bounds.Left;
        var y = point.Y - bounds.Top;
        return x < 0 || y < 0 || x > bounds.Width || y > bounds.Height ? null : (x, y);
    }

    private static string ProcessNameOf(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "the front window";
        }
        catch (InvalidOperationException)
        {
            return "the front window";
        }
    }

    private static string GetClassName(IntPtr window)
    {
        var buffer = new char[256];
        var length = GetClassNameW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetWindowTitle(IntPtr window)
    {
        var buffer = new char[512];
        var length = GetWindowTextW(window, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, [Out] char[] buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, [Out] char[] buffer, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public NativeRect rcCaret;
    }
}
