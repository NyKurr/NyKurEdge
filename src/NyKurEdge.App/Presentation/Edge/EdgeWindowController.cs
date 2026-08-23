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
    private const int ExtendedStyleIndex = -20;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const int AlternateFillMode = 1;

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
#if NYKUR_EDGE_VISUAL_TEST
    private EdgeSide? _visualInspectionSide;
#endif
    private DisplayInfo _display;
    private double _animationFrom;
    private double _animationTo;
    private double _progress;
    private bool _isSettingsInteractive;
    private bool _canApplyWindowRegion;
    private bool _collapsedRegionApplied;
    private int _regionWidth;
    private int _regionHeight;
    private EdgeSide _regionSide;
    private int _regionThickness;
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
        UpdateBounds(0);

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

    public void EnableLocalizedRegion()
    {
        ThrowIfDisposed();
        _canApplyWindowRegion = true;
        UpdateBounds(_progress);
    }

#if NYKUR_EDGE_VISUAL_TEST
    public void SetVisualInspectionStatus(string status)
    {
        ThrowIfDisposed();
        _appWindow.Title = status;
    }

    public void SetVisualInspectionSide(EdgeSide side)
    {
        ThrowIfDisposed();
        _visualInspectionSide = side;
        UpdateBounds(_progress);
    }
#endif

    public void SetExpanded(bool expanded, bool immediate = false)
    {
        ThrowIfDisposed();
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
        UpdateBounds(_progress);
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
        UpdateBounds(_progress);
        ExpansionProgressChanged?.Invoke(this, _progress);
    }

    private void UpdateBounds(double expansionProgress)
    {
        var latestDisplay = _displayService.GetPrimaryDisplay();
        _display = latestDisplay;

        var bounds = EdgeWindowLayout.Calculate(
            _display.WorkArea,
            _display.Dpi,
            EffectiveSide,
            expansionProgress);
        _appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        if (_canApplyWindowRegion)
        {
            ApplyWindowRegion(bounds.Width, bounds.Height, expansionProgress);
        }
    }

    private void OnDisplayPollTick(DispatcherQueueTimer sender, object args)
    {
        var current = _displayService.GetPrimaryDisplay();
        if (current.WorkArea != _display.WorkArea || current.Dpi != _display.Dpi)
        {
            _display = current;
            UpdateBounds(_progress);
        }
    }

    private void ApplyWindowRegion(int width, int height, double expansionProgress)
    {
        if (expansionProgress > 0.001)
        {
            if (_collapsedRegionApplied)
            {
                _ = SetWindowRegion(_windowHandle, IntPtr.Zero, redraw: true);
                _collapsedRegionApplied = false;
            }

            return;
        }

        var side = EffectiveSide;
        var thickness = _settings.Current.Appearance.EdgeThickness;
        if (_collapsedRegionApplied &&
            width == _regionWidth &&
            height == _regionHeight &&
            side == _regionSide &&
            thickness == _regionThickness)
        {
            return;
        }

        var scale = _display.Dpi > 0 ? _display.Dpi / 96d : 1d;
        const int profilePointCount = 33;
        var points = new NativePoint[profilePointCount + 2];
        var outerX = side == EdgeSide.Right ? width - 1 : 0;
        points[0] = new NativePoint(outerX, 0);
        for (var index = 0; index < profilePointCount; index++)
        {
            var normalizedY = index / (double)(profilePointCount - 1);
            var upperLobe = Math.Exp(-Math.Pow((normalizedY - 0.34) * 6.2, 2));
            var lowerLobe = Math.Exp(-Math.Pow((normalizedY - 0.66) * 6.2, 2));
            var bubbleEnvelope = Math.Exp(-Math.Pow((normalizedY - 0.5) * 13.5, 2));
            var reachDip =
                (thickness * 0.72) +
                (Math.Max(upperLobe, lowerLobe) * 58) +
                (bubbleEnvelope * 30);
            var reach = Math.Clamp((int)Math.Round(reachDip * scale), 2, width);
            var x = side == EdgeSide.Right ? width - reach : reach - 1;
            var y = Math.Clamp((int)Math.Round(normalizedY * (height - 1)), 0, height - 1);
            points[index + 1] = new NativePoint(x, y);
        }

        points[^1] = new NativePoint(outerX, height - 1);
        var region = CreatePolygonRegion(points, points.Length, AlternateFillMode);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRegion(_windowHandle, region, redraw: true) == 0)
        {
            _ = DeleteObject(region);
            return;
        }

        _collapsedRegionApplied = true;
        _regionWidth = width;
        _regionHeight = height;
        _regionSide = side;
        _regionThickness = thickness;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private EdgeSide EffectiveSide
    {
        get
        {
#if NYKUR_EDGE_VISUAL_TEST
            return _visualInspectionSide ?? _settings.Current.EdgeSide;
#else
            return _settings.Current.EdgeSide;
#endif
        }
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

    [DllImport("gdi32.dll", EntryPoint = "CreatePolygonRgn")]
    private static extern IntPtr CreatePolygonRegion(
        [In] NativePoint[] points,
        int pointCount,
        int fillMode);

    [DllImport("user32.dll", EntryPoint = "SetWindowRgn")]
    private static extern int SetWindowRegion(
        IntPtr windowHandle,
        IntPtr region,
        [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }
}
