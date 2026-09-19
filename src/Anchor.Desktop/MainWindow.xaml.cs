using System.Runtime.InteropServices;
using Anchor.Infrastructure.Windows;
using Anchor.Core.Services;
using Anchor_Desktop.Services;
using Anchor_Desktop.ViewModels;
using Microsoft.UI.Xaml;

namespace Anchor_Desktop;

public sealed partial class MainWindow : Window
{
    private const int GwlWndProc = -4;
    private const uint WmInput = 0x00FF;
    private const uint WmHotkey = 0x0312;
    private const uint WmKeyDown = 0x0100;
    private const int VkEscape = 0x1B;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const int ManualRecoveryHotkey = 1;
    private const int ShowWindowHotkey = 2;
    private readonly WndProc _windowProcedure;
    private IntPtr _windowHandle;
    private IntPtr _originalProcedure;
    private bool _exitRequested;
    private bool _cleanupComplete;
    private readonly AppLifecycleModel _lifecycle = new();
    private readonly TrayIconService _tray;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Anchor Settings";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        RootFrame.Navigate(typeof(MainPage));

        _tray = new TrayIconService(GetTraySummary);
        _tray.ShowSettingsRequested += (_, _) => App.DispatcherQueue.TryEnqueue(ShowSettings);
        _tray.ReportDistractedRequested += (_, _) => App.DispatcherQueue.TryEnqueue(ReportDistractedFromTray);
        _tray.EmergencyReleaseRequested += (_, _) => App.DispatcherQueue.TryEnqueue(() =>
            App.Services.Watchdog.Signal(SafetyReleaseReason.Escape));
        _tray.ExitRequested += (_, _) => App.DispatcherQueue.TryEnqueue(async () => await ShutdownAsync());

        _windowProcedure = WindowMessage;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _originalProcedure = SetWindowLongPtr(
            _windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProcedure));
        RegisterHotKey(_windowHandle, ManualRecoveryHotkey, ModControl | ModShift, 0x7B); // F12
        RegisterHotKey(_windowHandle, ShowWindowHotkey, ModControl | ModShift, 0x41); // A
        App.Services.Sensors.AttachWindow(_windowHandle);

        AppWindow.Closing += AppWindow_Closing;
        Closed += Window_Closed;
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (!_exitRequested)
        {
            args.Cancel = true;
            _lifecycle.CloseSettings(App.Services.Orchestrator.IsRunning);
            AppWindow.Hide();
        }
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        if (!_cleanupComplete)
        {
            _ = ShutdownAfterUnexpectedCloseAsync();
        }
    }

    private void ReleaseWindowHooks()
    {
        UnregisterHotKey(_windowHandle, ManualRecoveryHotkey);
        UnregisterHotKey(_windowHandle, ShowWindowHotkey);
        if (_originalProcedure != IntPtr.Zero)
        {
            SetWindowLongPtr(_windowHandle, GwlWndProc, _originalProcedure);
            _originalProcedure = IntPtr.Zero;
        }
    }

    private IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmInput)
        {
            if (App.Services.Sensors.ProcessRawInput(lParam) == VkEscape
                && App.Services.Overlays.HasAnyOverlay)
            {
                App.DispatcherQueue.TryEnqueue(() => App.Services.Watchdog.Signal(SafetyReleaseReason.Escape));
            }
        }
        else if (message == WmKeyDown && wParam.ToInt32() == VkEscape)
        {
            App.Services.Watchdog.Signal(SafetyReleaseReason.Escape);
        }
        else if (message == WmHotkey)
        {
            var identifier = wParam.ToInt32();
            if (identifier == ManualRecoveryHotkey)
            {
                App.DispatcherQueue.TryEnqueue(async () =>
                {
                    if (RootFrame.Content is MainPage page)
                    {
                        await page.ViewModel.ReportDistractedCommand.ExecuteAsync(null);
                    }
                });
            }
            else if (identifier == ShowWindowHotkey)
            {
                App.DispatcherQueue.TryEnqueue(ShowSettings);
            }
        }

        return CallWindowProc(_originalProcedure, window, message, wParam, lParam);
    }

    private TrayTaskSummary GetTraySummary()
    {
        if (RootFrame.Content is not MainPage page)
        {
            return new("Anchor", "Open Settings", false);
        }
        return new(page.ViewModel.TaskTitle, page.ViewModel.CurrentSubtask, page.ViewModel.IsRunning);
    }

    private void ShowSettings()
    {
        _lifecycle.ShowSettings();
        AppWindow.Show();
        Activate();
    }

    private async void ReportDistractedFromTray()
    {
        if (RootFrame.Content is MainPage page)
        {
            await page.ViewModel.ReportDistractedCommand.ExecuteAsync(null);
        }
    }

    private async Task ShutdownAsync()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _lifecycle.RequestExit();
        _tray.Dispose();
        if (RootFrame.Content is MainPage page)
        {
            await page.ViewModel.DisposeAsync();
        }
        await App.Services.DisposeAsync();
        ReleaseWindowHooks();
        _cleanupComplete = true;
        Close();
    }

    internal Task RequestExitAsync() => ShutdownAsync();

    private async Task ShutdownAfterUnexpectedCloseAsync()
    {
        _exitRequested = true;
        _tray.Dispose();
        await App.Services.DisposeAsync();
        ReleaseWindowHooks();
        _cleanupComplete = true;
    }

    private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr newProcedure);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int identifier, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int identifier);

}
