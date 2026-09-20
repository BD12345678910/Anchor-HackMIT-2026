using Anchor.Core.Models;
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

    /// <summary>The 2-second sensing tick refreshes the scene; older scenes no longer describe the front window.</summary>
    private static readonly TimeSpan SceneLifetime = TimeSpan.FromSeconds(8);

    /// <summary>Title bar, tab strip and toolbars live in this band; pictures found only there are UI, not content.</summary>
    private const int ToolbarBandHeight = 96;

    private readonly Func<bool> _shouldRun;
    private readonly Action<string, Exception> _logError;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    private string _status = "Off";
    private int _visibleRegions;
    private Scene? _scene;
    private readonly Dictionary<string, PictureRelevance> _verdicts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingVerdicts = new(StringComparer.Ordinal);
    private int _gradingInFlight;
    private string _gradingSource = "not graded yet";
    private readonly Dictionary<string, TextRelevance> _textVerdicts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingTextVerdicts = new(StringComparer.Ordinal);
    private int _textGradingInFlight;

    /// <summary>OCR older than this no longer lines up with the page (the user scrolled); passages are not dimmed from it.</summary>
    private static readonly TimeSpan TextSnapshotLifetime = TimeSpan.FromSeconds(4);

    /// <summary>Alpha of the shade painted over off-task passages: legible if you look, but no longer pulling the eye.</summary>
    private const int DimAlpha = 150;
    private const int ThumbnailEdge = 224;

    /// <summary>What the sensing loop last learned about the front window; read by the blur thread.</summary>
    private sealed record Scene(
        DateTimeOffset At,
        bool WindowRelevant,
        string ProcessName,
        string? WindowTitle,
        string Goal,
        string CurrentSubtask,
        ScreenSnapshot? Screen)
    {
        public string PageKey => string.Join('|', Goal, CurrentSubtask, ProcessName, WindowTitle).ToLowerInvariant();
    }

    /// <summary>
    /// Grades pictures semantically (DeepSeek when configured, local vocabulary otherwise). Set by
    /// the view model; verdicts are cached per page so the grader is asked once per new picture.
    /// </summary>
    public Func<PictureGradingRequest, CancellationToken, Task<PictureGrading>>? Grader { get; set; }

    /// <summary>Grades on-screen passages (paragraphs, lists, cards) as part of the task or off-task; off-task ones are dimmed.</summary>
    public Func<TextGradingRequest, CancellationToken, Task<TextGrading>>? TextGrader { get; set; }

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

    /// <summary>
    /// Tells the blur pass how the front window relates to the task so each picture can be treated
    /// proportionally: softened when the grader says it illustrates the task, pixelated when it is
    /// unrelated content, and turned into a coarse mosaic when the window is off-task or the picture is bait.
    /// </summary>
    public void UpdateScene(bool windowRelevant, string? processName, string? windowTitle, string goal, string currentSubtask, ScreenSnapshot? screen)
    {
        Volatile.Write(ref _scene, new Scene(DateTimeOffset.UtcNow, windowRelevant, processName ?? string.Empty, windowTitle, goal, currentSubtask, screen));
    }

    /// <summary>Forgets every picture verdict (new goal or plan).</summary>
    public void ResetVerdicts()
    {
        lock (_verdicts)
        {
            _verdicts.Clear();
            _pendingVerdicts.Clear();
            _textVerdicts.Clear();
            _pendingTextVerdicts.Clear();
            _gradingSource = "not graded yet";
        }
    }

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
        ExcludeNonContent(regions, foreground, bounds, window.Handle);
        var scan = stopwatch.Elapsed;
        var dimmed = PlanDimming(processName, bounds.Size);
        if (regions.Count == 0 && dimmed.Count == 0)
        {
            window.Hide();
            _visibleRegions = 0;
            Status = $"Watching {processName} · no pictures or off-task passages on screen";
            return scan > SlowScanBackoff / 2 ? SlowScanBackoff : Interval;
        }

        var treatments = PlanTreatments(capture, regions, processName, bounds.Size);
        if (dimmed.Count == 0 && treatments.All(static t => t == PictureTreatment.Keep))
        {
            // Everything on screen belongs to the task: paint nothing at all.
            window.Hide();
            _visibleRegions = 0;
            Status = $"Watching {processName} · {regions.Count} picture{(regions.Count == 1 ? string.Empty : "s")} on topic, left sharp · {_gradingSource}";
            return scan > SlowScanBackoff / 2 ? SlowScanBackoff : Interval;
        }

        using var surface = Compose(capture, regions, treatments, dimmed);
        window.Present(surface, bounds.Location);
        _visibleRegions = regions.Count + dimmed.Count;
        Status = DescribeTreatments(treatments, dimmed.Count, processName, _gradingSource);
        return scan > SlowScanBackoff / 2 ? SlowScanBackoff : Interval;
    }

    /// <summary>
    /// Window-local rectangles of passages the grader marked off-task. Only for the front window the
    /// sensing loop just read (same process, OCR a few seconds old at most) and only while that window
    /// is on-task overall; off-task windows are handled by the whole-window interventions instead.
    /// </summary>
    private List<Rectangle> PlanDimming(string processName, Size windowSize)
    {
        var result = new List<Rectangle>();
        var scene = Volatile.Read(ref _scene);
        var now = DateTimeOffset.UtcNow;
        if (scene is null || now - scene.At > SceneLifetime || !scene.WindowRelevant
            || scene.Screen is not { } screen || now - screen.Timestamp > TextSnapshotLifetime
            || !string.Equals(scene.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(screen.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        var blocks = TextBlockPlanner.Group(screen.Lines);
        if (blocks.Count == 0)
        {
            return result;
        }
        var missing = new List<TextBlockDescriptor>();
        lock (_verdicts)
        {
            foreach (var block in blocks)
            {
                var cacheKey = scene.PageKey + '|' + block.Key;
                if (_textVerdicts.TryGetValue(cacheKey, out var verdict))
                {
                    if (verdict == TextRelevance.OffTask)
                    {
                        var rect = Rectangle.Intersect(
                            Rectangle.FromLTRB((int)Math.Floor(block.Left) - 4, (int)Math.Floor(block.Top) - 2,
                                (int)Math.Ceiling(block.Left + block.Width) + 4, (int)Math.Ceiling(block.Top + block.Height) + 2),
                            new Rectangle(Point.Empty, windowSize));
                        if (rect.Width > 0 && rect.Height > 0 && rect.Bottom > ToolbarBandHeight)
                        {
                            result.Add(rect);
                        }
                    }
                }
                else if (_pendingTextVerdicts.Add(cacheKey))
                {
                    missing.Add(block);
                }
            }
        }
        if (missing.Count > 0)
        {
            RequestTextGrading(scene, missing);
        }
        return result;
    }

    private void RequestTextGrading(Scene scene, List<TextBlockDescriptor> blocks)
    {
        if (TextGrader is not { } grader || Interlocked.CompareExchange(ref _textGradingInFlight, 1, 0) != 0)
        {
            lock (_verdicts)
            {
                foreach (var block in blocks)
                {
                    _pendingTextVerdicts.Remove(scene.PageKey + '|' + block.Key);
                }
            }
            return;
        }

        var request = new TextGradingRequest(scene.Goal, scene.CurrentSubtask, scene.ProcessName, scene.WindowTitle, blocks);
        _ = Task.Run(async () =>
        {
            try
            {
                var grading = await grader(request, _cts.Token);
                lock (_verdicts)
                {
                    foreach (var block in blocks)
                    {
                        var cacheKey = scene.PageKey + '|' + block.Key;
                        _pendingTextVerdicts.Remove(cacheKey);
                        if (grading.Verdicts.TryGetValue(block.Key, out var verdict))
                        {
                            _textVerdicts[cacheKey] = verdict;
                        }
                    }
                }
                _wake.Set();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logError("passage grading", error);
                lock (_verdicts)
                {
                    foreach (var block in blocks)
                    {
                        _pendingTextVerdicts.Remove(scene.PageKey + '|' + block.Key);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _textGradingInFlight, 0);
            }
        });
    }

    private PictureTreatment[] PlanTreatments(Bitmap capture, IReadOnlyList<Rectangle> regions, string processName, Size windowSize)
    {
        if (regions.Count == 0)
        {
            return [];
        }
        var scene = Volatile.Read(ref _scene);
        var fresh = scene is not null && DateTimeOffset.UtcNow - scene.At <= SceneLifetime;
        // No session running (toolkit preview): nothing is known about the task, so every picture
        // is pixelated to show what the tool does.
        var sameWindow = fresh && string.Equals(scene!.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        var screenMatches = sameWindow && scene!.Screen is { } shot
            && string.Equals(shot.ProcessName, processName, StringComparison.OrdinalIgnoreCase);
        var context = !fresh
            ? new PictureSceneContext(true, [], [], windowSize.Width, windowSize.Height)
            : sameWindow
            ? new PictureSceneContext(scene!.WindowRelevant, [], screenMatches ? scene.Screen!.Lines : [], windowSize.Width, windowSize.Height)
            : PictureSceneContext.OffTask(windowSize.Width, windowSize.Height);

        var descriptors = new PictureDescriptor[regions.Count];
        var treatments = new PictureTreatment[regions.Count];
        var missing = new List<PictureDescriptor>();
        var pageKey = sameWindow ? scene!.PageKey : string.Empty;
        lock (_verdicts)
        {
            for (var i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                descriptors[i] = PictureTreatmentPlanner.Describe(new PixelRect(r.X, r.Y, r.Width, r.Height), context, PixelHash(capture, r));
                PictureRelevance? verdict = null;
                if (sameWindow && context.WindowRelevant)
                {
                    var cacheKey = pageKey + '|' + descriptors[i].Key;
                    if (_verdicts.TryGetValue(cacheKey, out var known))
                    {
                        verdict = known;
                    }
                    // Grade only once the page text has been read, so the grader sees captions and
                    // page content rather than a bare window title. The grader also gets the pixels.
                    else if (screenMatches && _pendingVerdicts.Add(cacheKey))
                    {
                        missing.Add(descriptors[i] with { ThumbnailDataUrl = Thumbnail(capture, r) });
                    }
                }
                treatments[i] = fresh
                    ? PictureTreatmentPlanner.Plan(descriptors[i], context, verdict)
                    : descriptors[i].AdShaped ? PictureTreatment.Mosaic : PictureTreatment.Pixelate;
            }
        }

        if (missing.Count > 0)
        {
            RequestGrading(scene!, missing);
        }
        return treatments;
    }

    private void RequestGrading(Scene scene, List<PictureDescriptor> pictures)
    {
        if (Grader is not { } grader || Interlocked.CompareExchange(ref _gradingInFlight, 1, 0) != 0)
        {
            // Another request is in flight; release the reservations so the next tick retries.
            lock (_verdicts)
            {
                foreach (var picture in pictures)
                {
                    _pendingVerdicts.Remove(scene.PageKey + '|' + picture.Key);
                }
            }
            return;
        }

        var request = new PictureGradingRequest(
            scene.Goal,
            scene.CurrentSubtask,
            scene.ProcessName,
            scene.WindowTitle,
            scene.Screen?.Excerpt ?? string.Empty,
            pictures);
        _ = Task.Run(async () =>
        {
            try
            {
                var grading = await grader(request, _cts.Token);
                lock (_verdicts)
                {
                    foreach (var picture in pictures)
                    {
                        var cacheKey = scene.PageKey + '|' + picture.Key;
                        _pendingVerdicts.Remove(cacheKey);
                        if (grading.Verdicts.TryGetValue(picture.Key, out var verdict))
                        {
                            _verdicts[cacheKey] = verdict;
                        }
                    }
                    _gradingSource = grading.IsFallback ? grading.Source : "graded by DeepSeek";
                }
                _wake.Set();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _logError("picture grading", error);
                lock (_verdicts)
                {
                    foreach (var picture in pictures)
                    {
                        _pendingVerdicts.Remove(scene.PageKey + '|' + picture.Key);
                    }
                    _gradingSource = $"grading failed: {error.GetType().Name}";
                }
            }
            finally
            {
                Interlocked.Exchange(ref _gradingInFlight, 0);
            }
        });
    }

    private static string DescribeTreatments(IReadOnlyList<PictureTreatment> treatments, int dimmedPassages, string processName, string gradingSource)
    {
        var kept = treatments.Count(static t => t == PictureTreatment.Keep);
        var pixelated = treatments.Count(static t => t == PictureTreatment.Pixelate);
        var mosaicked = treatments.Count - kept - pixelated;
        var parts = new List<string>(4);
        if (dimmedPassages > 0)
        {
            parts.Add($"{dimmedPassages} off-task passage{(dimmedPassages == 1 ? string.Empty : "s")} dimmed");
        }
        if (kept > 0)
        {
            parts.Add($"{kept} on-topic left sharp");
        }
        if (pixelated > 0)
        {
            parts.Add($"{pixelated} unrelated pixelated");
        }
        if (mosaicked > 0)
        {
            parts.Add($"{mosaicked} off-task/ad-like mosaicked");
        }
        return $"{string.Join(" · ", parts)} in {processName} · {gradingSource}";
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

    /// <summary>
    /// Drops window chrome (toolbar band), anything hidden behind the taskbar, and anything under one
    /// of Anchor's own overlays, so only document pictures are pixelated.
    /// </summary>
    private void ExcludeNonContent(List<Rectangle> regions, IntPtr foreground, Rectangle bounds, IntPtr blurWindow)
    {
        var visible = new Rectangle(Point.Empty, bounds.Size);
        var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfoW(monitor, ref info))
        {
            var work = Rectangle.FromLTRB(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
            work.Offset(-bounds.X, -bounds.Y);
            visible.Intersect(work);
        }

        // Anchor windows stacked above the foreground window (beacon, recovery card, gate): whatever they
        // cover is not page content. Full-window overlays such as the dim are ignored so they never veto everything.
        var own = new List<Rectangle>();
        for (var above = GetWindow(foreground, GwHwndPrev); above != IntPtr.Zero; above = GetWindow(above, GwHwndPrev))
        {
            if (above == blurWindow || !IsWindowVisible(above))
            {
                continue;
            }
            GetWindowThreadProcessId(above, out var pid);
            if (pid != _ownProcessId || !GetWindowRect(above, out var r))
            {
                continue;
            }
            var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            rect.Offset(-bounds.X, -bounds.Y);
            if (rect.Width > 0 && rect.Height > 0 && !rect.Contains(visible))
            {
                own.Add(rect);
            }
        }

        for (var i = regions.Count - 1; i >= 0; i--)
        {
            var region = Rectangle.Intersect(regions[i], visible);
            if (region.Width < 24 || region.Height < 24 || region.Bottom <= ToolbarBandHeight
                || own.Any(rect => rect.IntersectsWith(region)))
            {
                regions.RemoveAt(i);
                continue;
            }
            regions[i] = region;
        }
    }

    /// <summary>64-bit average hash of the region's luminance, so the same picture keeps its key while the page scrolls.</summary>
    private static string PixelHash(Bitmap capture, Rectangle region)
    {
        using var small = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(small))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(capture, new Rectangle(0, 0, 8, 8), region, GraphicsUnit.Pixel);
        }
        Span<int> luma = stackalloc int[64];
        var total = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var pixel = small.GetPixel(x, y);
                var value = (pixel.R * 299 + pixel.G * 587 + pixel.B * 114) / 1000;
                luma[y * 8 + x] = value;
                total += value;
            }
        }
        var mean = total / 64;
        ulong bits = 0;
        for (var i = 0; i < 64; i++)
        {
            if (luma[i] > mean)
            {
                bits |= 1UL << i;
            }
        }
        return bits.ToString("x16");
    }

    /// <summary>Small JPEG of the picture as a data URL (longest side <see cref="ThumbnailEdge"/> px) for the vision grader.</summary>
    private static string? Thumbnail(Bitmap capture, Rectangle region)
    {
        try
        {
            var scale = Math.Min(1.0, ThumbnailEdge / (double)Math.Max(region.Width, region.Height));
            var size = new Size(Math.Max(8, (int)(region.Width * scale)), Math.Max(8, (int)(region.Height * scale)));
            using var small = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(small))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(capture, new Rectangle(Point.Empty, size), region, GraphicsUnit.Pixel);
            }
            using var stream = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(static codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
            small.Save(stream, encoder, parameters);
            return "data:image/jpeg;base64," + Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (Exception error) when (error is ExternalException or ArgumentException)
        {
            return null;
        }
    }

    private static Bitmap Compose(Bitmap capture, IReadOnlyList<Rectangle> regions, IReadOnlyList<PictureTreatment> treatments, IReadOnlyList<Rectangle> dimmed)
    {
        var surface = new Bitmap(capture.Width, capture.Height, PixelFormat.Format32bppPArgb);
        using var target = Graphics.FromImage(surface);
        target.InterpolationMode = InterpolationMode.NearestNeighbor;
        target.PixelOffsetMode = PixelOffsetMode.Half;
        target.CompositingQuality = CompositingQuality.HighSpeed;

        if (dimmed.Count > 0)
        {
            // Off-task passages: a translucent shade over the text, so it recedes without disappearing.
            using var shade = new SolidBrush(Color.FromArgb(DimAlpha, 24, 26, 34));
            foreach (var rect in dimmed)
            {
                target.FillRectangle(shade, rect);
            }
        }

        for (var i = 0; i < regions.Count; i++)
        {
            var local = regions[i];
            // Downsample to a coarse grid and stretch back with nearest-neighbour: the picture
            // stays recognisable at low resolution, but fine detail that pulls attention is gone.
            // The grid is finer for pictures that illustrate the task and coarser for ads/off-task pages.
            var cells = PictureTreatmentPlanner.CellsAcrossShortSide(treatments[i]);
            if (cells <= 0)
            {
                // The picture illustrates the task: nothing is painted over it.
                continue;
            }
            var shrink = Math.Clamp(Math.Min(local.Width, local.Height) / cells, 3, 48);
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

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private const uint GwHwndPrev = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

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
