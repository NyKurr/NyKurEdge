using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;
using Windows.Graphics;

namespace NyKurEdge.App.Presentation.Edge;

public sealed class EdgeWindowController : IDisposable
{
    private const int ExpandedWidthDip = 388;
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    private const uint SetWindowPositionShowWindow = 0x0040;

    private static readonly IntPtr TopMostWindow = new(-1);

    private readonly Window _window;
    private readonly IDisplayService _displayService;
    private readonly SettingsService _settings;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly DispatcherQueueTimer _displayPollTimer;
    private readonly Stopwatch _animationClock = new();
    private readonly bool _visualInspectionMode;
    private DisplayInfo _display;
    private double _animationFrom;
    private double _animationTo;
    private double _progress;
    private bool _isSettingsInteractive;
    private bool _disposed;

    public EdgeWindowController(
        Window window,
        IDisplayService displayService,
        SettingsService settings)
    {
        _window = window;
        _displayService = displayService;
        _settings = settings;
        _appWindow = window.AppWindow;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _display = displayService.GetPrimaryDisplay();
#if NYKUR_EDGE_VISUAL_TEST
        _visualInspectionMode = true;
#elif DEBUG
        _visualInspectionMode = string.Equals(
            Environment.GetEnvironmentVariable("NYKUR_EDGE_VISUAL_TEST"),
            "1",
            StringComparison.Ordinal);
#endif

        ConfigurePresenter();
        ApplyNativeWindowStyles(noActivate: true);
        UpdateBounds(GetCollapsedWidthDip());

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _animationTimer = dispatcher.CreateTimer();
        _animationTimer.Interval = TimeSpan.FromMilliseconds(16);
        _animationTimer.Tick += OnAnimationTick;

        _displayPollTimer = dispatcher.CreateTimer();
        _displayPollTimer.Interval = TimeSpan.FromSeconds(4);
        _displayPollTimer.IsRepeating = true;
        _displayPollTimer.Tick += OnDisplayPollTick;
        _displayPollTimer.Start();
    }

    public event EventHandler<double>? ExpansionProgressChanged;

    public bool IsExpanded => _progress >= 0.999;

    public void ShowWithoutActivation()
    {
        ThrowIfDisposed();
        if (_visualInspectionMode)
        {
            ApplyNativeWindowStyles(noActivate: false);
            _appWindow.Show(activateWindow: true);
            _window.Activate();
            SetExpanded(expanded: true, immediate: true);
            return;
        }

        ApplyNativeWindowStyles(noActivate: true);
        _appWindow.Show(activateWindow: false);
        _ = SetWindowPos(
            _windowHandle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate |
            SetWindowPositionFrameChanged |
            SetWindowPositionShowWindow);
    }

    public void SetExpanded(bool expanded, bool immediate = false)
    {
        ThrowIfDisposed();
        if (_visualInspectionMode && !expanded)
        {
            return;
        }

        var target = expanded ? 1d : 0d;
        if (Math.Abs(target - _progress) < 0.001)
        {
            return;
        }

        _animationTimer.Stop();
        _animationFrom = _progress;
        _animationTo = target;
        _animationClock.Restart();

        if (immediate)
        {
            SetProgress(target);
            return;
        }

        _animationTimer.Start();
    }

    public void ApplySettings()
    {
        ThrowIfDisposed();
        UpdateBounds(GetWidthForProgress(_progress));
    }

    public void SetSettingsInteraction(bool enabled)
    {
        ThrowIfDisposed();
        if (_isSettingsInteractive == enabled)
        {
            return;
        }

        _isSettingsInteractive = enabled;
        ApplyNativeWindowStyles(noActivate: !enabled);
        if (enabled)
        {
            _window.Activate();
        }
        else
        {
            _ = SetWindowPos(
                _windowHandle,
                TopMostWindow,
                0,
                0,
                0,
                0,
                SetWindowPositionNoMove | SetWindowPositionNoSize | SetWindowPositionNoActivate);
        }
    }

    public void CloseWindow()
    {
        ThrowIfDisposed();
        _window.Close();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _animationTimer.Stop();
        _animationTimer.Tick -= OnAnimationTick;
        _displayPollTimer.Stop();
        _displayPollTimer.Tick -= OnDisplayPollTick;
    }

    private void ConfigurePresenter()
    {
        _appWindow.IsShownInSwitchers = _visualInspectionMode;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
    }

    private void ApplyNativeWindowStyles(bool noActivate)
    {
        var style = GetWindowLongPointer(_windowHandle, ExtendedStyleIndex).ToInt64();
        if (_visualInspectionMode)
        {
            style &= ~ExtendedStyleToolWindow;
            style |= ExtendedStyleAppWindow;
            noActivate = false;
        }
        else
        {
            style |= ExtendedStyleToolWindow;
            style &= ~ExtendedStyleAppWindow;
        }

        style = noActivate
            ? style | ExtendedStyleNoActivate
            : style & ~ExtendedStyleNoActivate;
        _ = SetWindowLongPointer(_windowHandle, ExtendedStyleIndex, new IntPtr(style));

        _ = SetWindowPos(
            _windowHandle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate |
            SetWindowPositionFrameChanged);
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        const double durationMilliseconds = 238;
        var elapsed = _animationClock.Elapsed.TotalMilliseconds;
        var time = Math.Clamp(elapsed / durationMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - time, 3);
        SetProgress(_animationFrom + ((_animationTo - _animationFrom) * eased));

        if (time >= 1)
        {
            _animationTimer.Stop();
            _animationClock.Stop();
            SetProgress(_animationTo);
        }
    }

    private void SetProgress(double progress)
    {
        _progress = Math.Clamp(progress, 0, 1);
        UpdateBounds(GetWidthForProgress(_progress));
        ExpansionProgressChanged?.Invoke(this, _progress);
    }

    private double GetWidthForProgress(double progress) =>
        GetCollapsedWidthDip() + ((ExpandedWidthDip - GetCollapsedWidthDip()) * progress);

    private int GetCollapsedWidthDip() => _settings.Current.Appearance.EdgeThickness;

    private void UpdateBounds(double widthDip)
    {
        var latestDisplay = _displayService.GetPrimaryDisplay();
        _display = latestDisplay;

        var scale = Math.Max(_display.Dpi / 96d, 1);
        var width = Math.Max(1, (int)Math.Round(widthDip * scale));
        var workArea = _display.WorkArea;
        var x = _settings.Current.EdgeSide == EdgeSide.Right
            ? workArea.X + workArea.Width - width
            : workArea.X;

        _appWindow.MoveAndResize(new RectInt32(x, workArea.Y, width, workArea.Height));
    }

    private void OnDisplayPollTick(DispatcherQueueTimer sender, object args)
    {
        var current = _displayService.GetPrimaryDisplay();
        if (current.WorkArea != _display.WorkArea || current.Dpi != _display.Dpi)
        {
            _display = current;
            UpdateBounds(GetWidthForProgress(_progress));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static IntPtr GetWindowLongPointer(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));

    private static IntPtr SetWindowLongPointer(IntPtr windowHandle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
