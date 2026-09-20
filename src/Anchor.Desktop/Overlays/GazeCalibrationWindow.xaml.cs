using Anchor.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Anchor_Desktop.Overlays;

/// <summary>
/// Full-screen calibration surface: a target appears, the user looks at it and clicks it, and the
/// gaze features captured at the moment of the click are paired with the target position. Clicking
/// is what makes the ground truth trustworthy — the eyes are on the dot when the pointer reaches it.
/// </summary>
public sealed partial class GazeCalibrationWindow : Window
{
    /// <summary>Targets cover the corners, edges, centre and the mid-ring so the fit is not extrapolating.</summary>
    public static readonly (double X, double Y)[] Targets =
    [
        (0.5, 0.5),
        (0.06, 0.08), (0.5, 0.06), (0.94, 0.08),
        (0.06, 0.5), (0.94, 0.5),
        (0.06, 0.92), (0.5, 0.94), (0.94, 0.92),
        (0.28, 0.28), (0.72, 0.28), (0.28, 0.72), (0.72, 0.72),
    ];

    private const double ClickTolerance = 90;

    private int _index;
    private bool _busy;
    private bool _finished;

    public GazeCalibrationWindow()
    {
        InitializeComponent();
        Root.KeyDown += OnKeyDown;
        Root.SizeChanged += (_, _) => PlaceTarget();
        Root.Loaded += (_, _) =>
        {
            Root.Focus(FocusState.Programmatic);
            PlaceTarget();
        };
        Closed += (_, _) => Report("Calibration cancelled — the previous calibration is unchanged.");
        UpdateProgress();
    }

    /// <summary>Captures the gaze features held right now for the given screen position.</summary>
    public Func<double, double, Task<CalibrationProgress>>? Capture { get; set; }

    /// <summary>Fits the model once every target has been clicked.</summary>
    public Func<Task<CalibrationResult>>? Finish { get; set; }

    /// <summary>Reports the closing status back to the page (cancelled, failed or the accuracy achieved).</summary>
    public Action<string>? Closing { get; set; }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Escape)
        {
            return;
        }
        Report("Calibration cancelled — the previous calibration is unchanged.");
        Close();
    }

    private async void Root_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_busy || _finished || Capture is null)
        {
            return;
        }
        var point = args.GetCurrentPoint(Root).Position;
        var (targetX, targetY) = Targets[_index];
        var centreX = targetX * Root.ActualWidth;
        var centreY = targetY * Root.ActualHeight;
        if (Math.Abs(point.X - centreX) > ClickTolerance || Math.Abs(point.Y - centreY) > ClickTolerance)
        {
            StatusText.Text = "Click the dot itself, not the background.";
            return;
        }

        _busy = true;
        try
        {
            var progress = await Capture(targetX, targetY);
            if (!progress.Accepted)
            {
                StatusText.Text = $"Not captured: {progress.Error} Look straight at the dot, hold still for a moment, then click again.";
                return;
            }
            StatusText.Text = string.Empty;
            _index++;
            if (_index < Targets.Length)
            {
                UpdateProgress();
                PlaceTarget();
                return;
            }
            _finished = true;
            UpdateProgress();
            var result = Finish is null ? null : await Finish();
            Report(result switch
            {
                null => "Calibration captured but the model could not be fitted.",
                { Accepted: true } => $"Calibration ready · {result.TargetCount} targets · {result.InlierCount}/{result.SampleCount} samples kept · median error {result.MedianError:P1} of the screen (max {result.MaxError:P1}).",
                _ => $"Calibration failed: {result.Error}"
            });
            Close();
        }
        catch (Exception error)
        {
            Report($"Calibration failed: {error.Message}");
            Close();
        }
        finally
        {
            _busy = false;
        }
    }

    private void Report(string message)
    {
        var report = Closing;
        Closing = null;
        report?.Invoke(message);
    }

    private void UpdateProgress() =>
        ProgressText.Text = _finished
            ? "Fitting your calibration…"
            : $"Point {_index + 1} of {Targets.Length}";

    private void PlaceTarget()
    {
        if (_index >= Targets.Length || Root.ActualWidth <= 0)
        {
            return;
        }
        var (x, y) = Targets[_index];
        Place(Halo, x, y);
        Place(Target, x, y);
        Place(Pupil, x, y);
    }

    private void Place(FrameworkElement element, double normalizedX, double normalizedY)
    {
        Canvas.SetLeft(element, (normalizedX * Root.ActualWidth) - (element.Width / 2));
        Canvas.SetTop(element, (normalizedY * Root.ActualHeight) - (element.Height / 2));
    }
}
