using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;
using Windows.Graphics;
using Windows.UI;
using WinRT;

namespace NyKurEdge.App.Presentation.Edge;

public sealed class EdgeWindowController : IDisposable
{
    private const int ExtendedStyleIndex = -20;
    private const int WindowStyleIndex = -16;
    private const long WindowStyleBorder = 0x00800000L;
    private const long WindowStyleDialogFrame = 0x00400000L;
    private const long WindowStyleCaption = 0x00C00000L;
    private const long WindowStyleThickFrame = 0x00040000L;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const int AlternateFillMode = 1;
    private const int RegionOr = 2;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const uint DwmDoNotRound = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;

    private static readonly IntPtr TopMostWindow = new(-1);

    private readonly Window _window;
    private readonly IDisplayService _displayService;
    private readonly SettingsService _settings;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly SystemBackdrop? _expandedBackdrop;
    private readonly DesktopAcrylicController? _acrylicController;
    private readonly SystemBackdropConfiguration? _backdropConfiguration;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly DispatcherQueueTimer _displayPollTimer;
    private readonly Stopwatch _animationClock = new();
    private readonly NativeEdgeCompositionHost? _nativeOverlay;
    private readonly bool _visualInspectionMode;
#if NYKUR_EDGE_VISUAL_TEST
    private EdgeSide? _visualInspectionSide;
#endif
    private DisplayInfo _display;
    private double _animationFrom;
    private double _animationTo;
    private double _progress;
    private bool _isSettingsInteractive;
    private bool _isPinnedInteractive;
    private bool _isExpandedBackdropApplied;
    private bool _canApplyAdaptiveRegion;
    private bool _windowRegionApplied;
    private int _regionWidth;
    private int _regionHeight;
    private EdgeSide _regionSide;
    private int _regionThickness;
    private int _regionProgressBucket = -1;
    private int _regionNotificationBucket = -1;
    private double _notificationExpansion;
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
        _expandedBackdrop = window.SystemBackdrop;
        if (DesktopAcrylicController.IsSupported())
        {
            _backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark,
            };
            _acrylicController = new DesktopAcrylicController();
            window.SystemBackdrop = null;
            _acrylicController.AddSystemBackdropTarget(
                window.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
        }
        _isExpandedBackdropApplied = _expandedBackdrop is not null;
        _display = displayService.GetPrimaryDisplay();
        _nativeOverlay = NativeEdgeCompositionHost.TryCreate();
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.PointerEntered += OnNativeOverlayPointerEntered;
            _nativeOverlay.PointerExited += OnNativeOverlayPointerExited;
            _nativeOverlay.Clicked += OnNativeOverlayClicked;
            _nativeOverlay.SecondaryClicked += OnNativeOverlaySecondaryClicked;
        }
#if NYKUR_EDGE_VISUAL_TEST
        _visualInspectionMode = true;
#elif DEBUG
        _visualInspectionMode = string.Equals(
            Environment.GetEnvironmentVariable("NYKUR_EDGE_VISUAL_TEST"),
            "1",
            StringComparison.Ordinal);
#else
        _visualInspectionMode = false;
#endif

        ConfigurePresenter();
        ApplyNativeWindowStyles(noActivate: true);
        SuppressNativeFrame();
        _window.Activated += OnWindowActivated;
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

    public event EventHandler? CollapsedPointerEntered;

    public event EventHandler? CollapsedPointerExited;

    public event EventHandler? CollapsedClicked;

    public event EventHandler? CollapsedSecondaryClicked;

    public bool IsExpanded => _progress >= 0.999;

    public void ShowWithoutActivation()
    {
        ThrowIfDisposed();
        if (_visualInspectionMode)
        {
            ApplyNativeWindowStyles(noActivate: false);
            _appWindow.Show(activateWindow: true);
            _window.Activate();
            SuppressNativeFrame();
            _ = DispatcherQueue.GetForCurrentThread().TryEnqueue(
                DispatcherQueuePriority.Low,
                SuppressNativeFrame);
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

    public void EnableAdaptiveRegion()
    {
        ThrowIfDisposed();
        _canApplyAdaptiveRegion = true;
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
        UpdateInteractionActivation();
    }

    public void SetPinnedInteraction(bool enabled)
    {
        ThrowIfDisposed();
        if (_isPinnedInteractive == enabled)
        {
            return;
        }

        _isPinnedInteractive = enabled;
        UpdateInteractionActivation();
    }

    public void SetNotificationExpansion(double progress)
    {
        ThrowIfDisposed();
        progress = Math.Clamp(progress, 0, 1);
        if (Math.Abs(progress - _notificationExpansion) < 0.006)
        {
            return;
        }

        _notificationExpansion = progress;
        if (_canApplyAdaptiveRegion && _regionWidth > 0 && _regionHeight > 0)
        {
            ApplyWindowRegion(_regionWidth, _regionHeight, _progress);
        }
    }

    internal void RenderCollapsedEdge(
        Vector2[] primary,
        Vector2[] secondary,
        Vector2[] interference,
        Vector2[] filament,
        Color accent,
        EdgeOrbScale orbScale,
        double notificationProgress,
        float energy)
    {
        if (_nativeOverlay is null || _disposed)
        {
            return;
        }

        _nativeOverlay.Render(
            primary,
            secondary,
            interference,
            filament,
            accent,
            orbScale,
            notificationProgress,
            energy);
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
        _window.Activated -= OnWindowActivated;
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.PointerEntered -= OnNativeOverlayPointerEntered;
            _nativeOverlay.PointerExited -= OnNativeOverlayPointerExited;
            _nativeOverlay.Clicked -= OnNativeOverlayClicked;
            _nativeOverlay.SecondaryClicked -= OnNativeOverlaySecondaryClicked;
            _nativeOverlay.Dispose();
        }
        _acrylicController?.RemoveAllSystemBackdropTargets();
        _acrylicController?.Dispose();
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
        var windowStyle = GetWindowLongPointer(_windowHandle, WindowStyleIndex).ToInt64();
        windowStyle &= ~(WindowStyleCaption |
                         WindowStyleThickFrame |
                         WindowStyleBorder |
                         WindowStyleDialogFrame);
        _ = SetWindowLongPointer(_windowHandle, WindowStyleIndex, new IntPtr(windowStyle));

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
        SuppressNativeFrame();
    }

    private void SuppressNativeFrame()
    {
        var cornerPreference = DwmDoNotRound;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(uint));

        var borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmBorderColor,
            ref borderColor,
            sizeof(uint));
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_backdropConfiguration is not null)
        {
            _backdropConfiguration.IsInputActive =
                args.WindowActivationState != WindowActivationState.Deactivated;
        }

        SuppressNativeFrame();
    }

    private void OnAnimationTick(DispatcherQueueTimer sender, object args)
    {
        var expanding = _animationTo > _animationFrom;
        var durationMilliseconds = expanding ? 286d : 226d;
        var elapsed = _animationClock.Elapsed.TotalMilliseconds;
        var time = Math.Clamp(elapsed / durationMilliseconds, 0, 1);
        var eased = expanding
            ? 1 - Math.Pow(1 - time, 4)
            : time * time * (3 - (2 * time));
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
        if (_nativeOverlay is not null)
        {
            var collapsedBounds = EdgeWindowLayout.Calculate(
                _display.WorkArea,
                _display.Dpi,
                EffectiveSide,
                0);
            _nativeOverlay.UpdateBounds(collapsedBounds, _display.Dpi, EffectiveSide);
            _nativeOverlay.SetExpansionProgress(expansionProgress);
        }
        _appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        SetBackdropVisible(expansionProgress > 0.001);
        if (_canApplyAdaptiveRegion)
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
        var progress = Math.Clamp(expansionProgress, 0, 1);
        var progressBucket = (int)Math.Round(progress * 60);
        var notificationBucket = (int)Math.Round(_notificationExpansion * 30);
        var side = EffectiveSide;
        var thickness = _settings.Current.Appearance.EdgeThickness;
        if (_windowRegionApplied &&
            width == _regionWidth &&
            height == _regionHeight &&
            side == _regionSide &&
            thickness == _regionThickness &&
            progressBucket == _regionProgressBucket &&
            notificationBucket == _regionNotificationBucket)
        {
            return;
        }

        var scale = _display.Dpi > 0 ? _display.Dpi / 96d : 1d;
        var composite = CreateRectRegion(0, 0, 0, 0);
        if (composite == IntPtr.Zero)
        {
            return;
        }

        var useNativeCollapsedSurface = _nativeOverlay is not null && progress <= 0.001;
        if (!useNativeCollapsedSurface)
        {
            var fluidField = CreateFluidFieldRegion(width, height, side, scale);
            UnionRegion(composite, fluidField);
        }

        var orbHalfWidth = Math.Max(
            1,
            (int)Math.Round((24 + (26 * _notificationExpansion)) * scale));
        var orbHalfHeight = Math.Max(
            1,
            (int)Math.Round((23 + (7 * _notificationExpansion)) * scale));
        var centerY = height / 2;
        if (!useNativeCollapsedSurface || _notificationExpansion > 0.035)
        {
            var orb = side == EdgeSide.Right
                ? CreateEllipticRegion(
                    width - orbHalfWidth,
                    centerY - orbHalfHeight,
                    width + orbHalfWidth,
                    centerY + orbHalfHeight)
                : CreateEllipticRegion(
                    -orbHalfWidth,
                    centerY - orbHalfHeight,
                    orbHalfWidth,
                    centerY + orbHalfHeight);
            UnionRegion(composite, orb);
        }

        if (progress > 0.001)
        {
            var bloom = CreatePanelBloomRegion(width, height, side, scale, progress);
            UnionRegion(composite, bloom);
        }

        if (SetWindowRegion(_windowHandle, composite, redraw: true) == 0)
        {
            _ = DeleteObject(composite);
            return;
        }

        _windowRegionApplied = true;
        _regionWidth = width;
        _regionHeight = height;
        _regionSide = side;
        _regionThickness = thickness;
        _regionProgressBucket = progressBucket;
        _regionNotificationBucket = notificationBucket;
    }

    private static IntPtr CreateFluidFieldRegion(
        int width,
        int height,
        EdgeSide side,
        double scale)
    {
        const int profilePointCount = 73;
        var points = new NativePoint[profilePointCount * 2];

        for (var index = 0; index < profilePointCount; index++)
        {
            var normalizedY = index / (double)(profilePointCount - 1);
            var edgeFade = SmootherStep(Math.Clamp(normalizedY / 0.105, 0, 1)) *
                           SmootherStep(Math.Clamp((1 - normalizedY) / 0.105, 0, 1));
            var centerEnvelope = Gaussian(normalizedY, 0.5, 2.05);
            var presence = edgeFade * (0.17 + (0.83 * centerEnvelope));
            var orbChannel = Gaussian(normalizedY, 0.5, 15.8);
            var orbShoulders =
                Gaussian(normalizedY, 0.435, 20.5) +
                Gaussian(normalizedY, 0.565, 20.5);
            var distantFlow =
                Gaussian(normalizedY, 0.285, 9.6) +
                Gaussian(normalizedY, 0.715, 9.6);
            var profileReachDip = presence *
                                  (2.4 +
                                   (21.5 * centerEnvelope) +
                                   (4.8 * orbShoulders) +
                                   (2.8 * distantFlow)) *
                                  (1 - (orbChannel * 0.36));
            var ribbonHalfWidthDip = 6.5 * Math.Sqrt(Math.Max(0, presence));
            var outerReachDip = profileReachDip + ribbonHalfWidthDip + presence;
            var innerReachDip = Math.Max(0, profileReachDip - ribbonHalfWidthDip);
            var outerReach = Math.Clamp((int)Math.Ceiling(outerReachDip * scale), 0, width);
            var innerReach = Math.Clamp((int)Math.Floor(innerReachDip * scale), 0, width);
            var y = Math.Clamp((int)Math.Round(normalizedY * (height - 1)), 0, height - 1);

            points[index] = new NativePoint(
                side == EdgeSide.Right ? width - outerReach : outerReach,
                y);
            var mirroredIndex = points.Length - 1 - index;
            points[mirroredIndex] = new NativePoint(
                side == EdgeSide.Right ? width - innerReach : innerReach,
                y);
        }

        return CreatePolygonRegion(points, points.Length, AlternateFillMode);
    }

    private static IntPtr CreatePanelBloomRegion(
        int width,
        int height,
        EdgeSide side,
        double scale,
        double progress)
    {
        const int profilePointCount = 65;
        var eased = 1 - Math.Pow(1 - progress, 3);
        var orbRadius = 22 * scale;
        var targetHeight = Math.Min(height, EdgeWindowLayout.ExpandedHeightDip * scale * 0.86);
        var bloomHeight = orbRadius * 2 + ((targetHeight - (orbRadius * 2)) * Math.Pow(eased, 0.72));
        var halfHeight = bloomHeight / 2;
        var centerY = height / 2d;
        var top = centerY - halfHeight;
        var bottom = centerY + halfHeight;
        var maximumReach = Math.Max(orbRadius, width - Math.Round(7 * scale));
        var points = new NativePoint[profilePointCount + 2];
        var edgeX = side == EdgeSide.Right ? width : 0;
        points[0] = new NativePoint(edgeX, (int)Math.Round(top));

        for (var index = 0; index < profilePointCount; index++)
        {
            var normalized = index / (double)(profilePointCount - 1);
            var signed = (normalized * 2) - 1;
            var edgeDistance = Math.Abs(signed);
            const double shoulderStart = 0.76;
            var shoulderProgress = Math.Clamp(
                (edgeDistance - shoulderStart) / (1 - shoulderStart),
                0,
                1);
            var capsule = edgeDistance <= shoulderStart
                ? 1
                : Math.Sqrt(Math.Max(0, 1 - (shoulderProgress * shoulderProgress)));
            var shoulder = Gaussian(normalized, 0.5, 2.1);
            var reach = orbRadius +
                        ((maximumReach - orbRadius) * capsule * (0.94 + (shoulder * 0.06)));
            var x = side == EdgeSide.Right
                ? width - (int)Math.Round(reach)
                : (int)Math.Round(reach);
            var y = (int)Math.Round(top + (normalized * bloomHeight));
            points[index + 1] = new NativePoint(x, y);
        }

        points[^1] = new NativePoint(edgeX, (int)Math.Round(bottom));
        return CreatePolygonRegion(points, points.Length, AlternateFillMode);
    }

    private static void UnionRegion(IntPtr destination, IntPtr addition)
    {
        if (addition == IntPtr.Zero)
        {
            return;
        }

        _ = CombineRegions(destination, destination, addition, RegionOr);
        _ = DeleteObject(addition);
    }

    private void UpdateInteractionActivation()
    {
        var interactive = _isSettingsInteractive || _isPinnedInteractive;
        ApplyNativeWindowStyles(noActivate: !interactive);
        if (interactive)
        {
            _window.Activate();
            return;
        }

        _ = SetWindowPos(
            _windowHandle,
            TopMostWindow,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove | SetWindowPositionNoSize | SetWindowPositionNoActivate);
    }

    private void SetBackdropVisible(bool visible)
    {
        if (_isExpandedBackdropApplied == visible)
        {
            return;
        }

        if (_acrylicController is not null)
        {
            ConfigureAcrylicMaterial(visible);
        }
        else if (_expandedBackdrop is not null)
        {
            // Older supported Windows builds retain the XAML backdrop rather
            // than exposing the opaque no-material fallback.
            _window.SystemBackdrop = _expandedBackdrop;
        }
        _isExpandedBackdropApplied = visible;
    }

    private void ConfigureAcrylicMaterial(bool expanded)
    {
        if (_acrylicController is null)
        {
            return;
        }

        _acrylicController.ResetProperties();
        _acrylicController.Kind = expanded ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin;
        if (expanded)
        {
            _acrylicController.TintColor = Windows.UI.Color.FromArgb(255, 7, 11, 17);
            _acrylicController.TintOpacity = 0.72f;
            _acrylicController.LuminosityOpacity = 0.62f;
            _acrylicController.FallbackColor = Windows.UI.Color.FromArgb(255, 9, 13, 19);
            return;
        }

        _acrylicController.TintOpacity = 0f;
    }

    private void OnNativeOverlayPointerEntered(object? sender, EventArgs args) =>
        CollapsedPointerEntered?.Invoke(this, EventArgs.Empty);

    private void OnNativeOverlayPointerExited(object? sender, EventArgs args) =>
        CollapsedPointerExited?.Invoke(this, EventArgs.Empty);

    private void OnNativeOverlayClicked(object? sender, EventArgs args) =>
        CollapsedClicked?.Invoke(this, EventArgs.Empty);

    private void OnNativeOverlaySecondaryClicked(object? sender, EventArgs args) =>
        CollapsedSecondaryClicked?.Invoke(this, EventArgs.Empty);

    private static double Gaussian(double value, double center, double sharpness) =>
        Math.Exp(-Math.Pow((value - center) * sharpness, 2));

    private static double SmootherStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * value * ((value * ((value * 6) - 15)) + 10);
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    [DllImport("gdi32.dll", EntryPoint = "CreatePolygonRgn")]
    private static extern IntPtr CreatePolygonRegion(
        [In] NativePoint[] points,
        int pointCount,
        int fillMode);

    [DllImport("gdi32.dll", EntryPoint = "CreateRectRgn")]
    private static extern IntPtr CreateRectRegion(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "CreateEllipticRgn")]
    private static extern IntPtr CreateEllipticRegion(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", EntryPoint = "CombineRgn")]
    private static extern int CombineRegions(
        IntPtr destination,
        IntPtr sourceOne,
        IntPtr sourceTwo,
        int combineMode);

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
