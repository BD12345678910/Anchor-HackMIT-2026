using Anchor.Core.Services;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Anchor_Desktop.Interventions;

/// <summary>
/// Blurs pictures in whatever window is in front, with no browser extension: the window is
/// captured with <c>PrintWindow</c>, photo-like areas are found by scoring the pixels
/// (<see cref="PictureRegionDetector"/>), and low-resolution (pixelated) copies of just those
/// regions are painted on a click-through layered overlay. Text stays crisp. Works for browsers, PDF viewers, Office
/// and any other window, because it never needs the app's cooperation.
/// </summary>
public sealed class DesktopImageBlurService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SlowScanBackoff = TimeSpan.FromMilliseconds(1500);
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Target number of mosaic cells across the shorter side of a picture.</summary>
    private const int CellsAcrossShortSide = 18;

    private readonly Func<bool> _shouldRun;
    private readonly Action<string, Exception> _logError;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private string _status = "Off";
    private int _visibleRegions;

    public DesktopImageBlurService(Func<bool> shouldRun, Action<string, Exception> logError)
    {
        _shouldRun = shouldRun;
        _logError = logError;
    }

    /// <summary>Human-readable state for the Tools page; updated from the worker thread.</summary>
    public string Status
    {
        get => _status;
        private set
        {
            if (string.Equals(_status, value, StringComparison.Ordinal))
            {
                return;
            }
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public int VisibleRegions => _visibleRegions;
    public bool IsBlurring => _visibleRegions > 0;
    public event EventHandler<string>? StatusChanged;

    /// <summary>Re-evaluates immediately (after a toggle or session change) instead of waiting a tick.</summary>
    public void Refresh()
    {
        if (_thread is null)
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "anchor-image-blur" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        _wake.Set();
    }

    private void Loop()
    {
        LayeredBlurWindow? window = null;
        try
        {
            window = new LayeredBlurWindow();
        }
        catch (InvalidOperationException error)
        {
            _logError("image-blur window", error);
            Status = $"Image blur unavailable: {error.Message}";
            return;
        }

        var wait = Interval;
        while (!_cts.IsCancellationRequested)
        {
            _wake.Wait(wait, CancellationToken.None);
            _wake.Reset();
            if (_cts.IsCancellationRequested)
            {
                break;
            }
            PumpMessages();
            try
            {
                wait = Tick(window);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                _logError("image-blur tick", error);
                window.Hide();
                _visibleRegions = 0;
                Status = $"Image blur paused: {error.GetType().Name}";
                wait = SlowScanBackoff;
            }
        }
        window.Dispose();
    }

    private TimeSpan Tick(LayeredBlurWindow window)
    {
        if (!_shouldRun())
        {
            window.Hide();
            _visibleRegions = 0;
            Status = "Idle · blurs pictures in the front window while a focus session runs";
            return Interval;
        }

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || IsIconic(foreground) || !GetWindowRect(foreground, out var native))
        {
            window.Hide();
            _visibleRegions = 0;
            return Interval;
        }
        GetWindowThreadProcessId(foreground, out var processId);
        var className = GetClassName(foreground);
        if (processId == _ownProcessId || ShellClasses.Contains(className, StringComparer.Ordinal))
        {
            window.Hide();
            _visibleRegions = 0;
            Status = "Waiting · no document in front";
            return Interval;
        }

        var bounds = Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            window.Hide();
            _visibleRegions = 0;
            return Interval;
        }

        var stopwatch = Stopwatch.StartNew();
        var processName = ProcessNameOf(processId);
        using var capture = Capture(foreground, bounds);
        if (capture is null)
        {
            window.Hide();
            _visibleRegions = 0;
            Status = $"Watching {processName} · window cannot be captured";
            return SlowScanBackoff;
        }

        var regions = FindRegions(capture);
        var scan = stopwatch.Elapsed;
        if (regions.Count == 0)
        {
            window.Hide();
            _visibleRegions = 0;
            Status = $"Watching {processName} · no pictures on screen";
            return scan > SlowScanBackoff / 2 ? SlowScanBackoff : Interval;
        }

        using var surface = Compose(capture, regions);
        window.Present(surface, bounds.Location);
        _visibleRegions = regions.Count;
        Status = $"Blurring {regions.Count} picture{(regions.Count == 1 ? string.Empty : "s")} in {processName}";
        return scan > SlowScanBackoff / 2 ? SlowScanBackoff : Interval;
    }

    private static Bitmap? Capture(IntPtr foreground, Rectangle bounds)
    {
        var capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
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

        if (captured)
        {
            return capture;
        }
        capture.Dispose();
        return null;
    }

    /// <summary>Window-local rectangles of picture-like areas in the capture.</summary>
    private static List<Rectangle> FindRegions(Bitmap capture)
    {
        var regions = new List<Rectangle>();
        var data = capture.LockBits(new Rectangle(Point.Empty, capture.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                var pixels = new ReadOnlySpan<byte>((byte*)data.Scan0, data.Stride * data.Height);
                foreach (var rect in PictureRegionDetector.Detect(pixels, data.Width, data.Height, data.Stride))
                {
                    regions.Add(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height));
                }
            }
        }
        finally
        {
            capture.UnlockBits(data);
        }
        return regions;
    }

    private static Bitmap Compose(Bitmap capture, IReadOnlyList<Rectangle> regions)
    {
        var surface = new Bitmap(capture.Width, capture.Height, PixelFormat.Format32bppPArgb);
        using var target = Graphics.FromImage(surface);
        target.InterpolationMode = InterpolationMode.NearestNeighbor;
        target.PixelOffsetMode = PixelOffsetMode.Half;
        target.CompositingQuality = CompositingQuality.HighSpeed;

        foreach (var local in regions)
        {
            // Downsample to a coarse grid and stretch back with nearest-neighbour: the picture
            // stays recognisable at low resolution, but fine detail that pulls attention is gone.
            var shrink = Math.Clamp(Math.Min(local.Width, local.Height) / CellsAcrossShortSide, 4, 40);
            var smallSize = new Size(Math.Max(2, local.Width / shrink), Math.Max(2, local.Height / shrink));
            using var small = new Bitmap(smallSize.Width, smallSize.Height, PixelFormat.Format32bppArgb);
            using (var shrinkGraphics = Graphics.FromImage(small))
            {
                shrinkGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                shrinkGraphics.PixelOffsetMode = PixelOffsetMode.Half;
                shrinkGraphics.DrawImage(capture, new Rectangle(Point.Empty, smallSize), local, GraphicsUnit.Pixel);
            }
            target.DrawImage(small, local);
        }
        return surface;
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

    private static void PumpMessages()
    {
        while (PeekMessageW(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref message);
            DispatchMessageW(ref message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
        _cts.Dispose();
    }

    private const uint PwRenderFullContent = 0x00000002;
    private const uint PmRemove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessageW(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref NativeMessage message);
}
