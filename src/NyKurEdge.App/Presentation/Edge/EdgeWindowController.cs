using System.Diagnostics;
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
    private const long ExtendedStyleTransparent = 0x00000020L;
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
    private const uint DwmBlurBehindEnable = 0x00000001;
    private const uint DwmBlurBehindRegion = 0x00000002;
    private const uint GetAncestorRoot = 2;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessageNonClientDestroy = 0x0082;
    private const uint WindowMessageNcHitTest = 0x0084;
    private const uint WindowMessageDwmCompositionChanged = 0x031E;
    private const int HitTestClient = 1;
    private const int HitTestTransparent = -1;
    private const double ExpandedBackdropStartProgress = 0.42;
    private static readonly UIntPtr WindowSubclassId = new(0x4E45);

    private static readonly IntPtr TopMostWindow = new(-1);

    private readonly Window _window;
    private readonly IDisplayService _displayService;
    private readonly SettingsService _settings;
    private readonly AppWindow _appWindow;
    private readonly IntPtr _windowHandle;
    private readonly SystemBackdrop? _expandedBackdrop;
    private readonly ICompositionSupportsSystemBackdrop _backdropTarget;
    private readonly IntPtr _backdropDispatcherQueueController;
    private readonly Windows.UI.Composition.Compositor _backdropCompositor;
    private readonly Windows.UI.Composition.CompositionColorBrush _collapsedBackdropBrush;
    private readonly DesktopAcrylicController? _acrylicController;
    private readonly SystemBackdropConfiguration? _backdropConfiguration;
    private readonly WindowSubclassProcedure _windowSubclassProcedure;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly DispatcherQueueTimer _displayPollTimer;
    private readonly Stopwatch _animationClock = new();
    private readonly NativeEdgeCompositionHost? _nativeOverlay;
    private readonly EdgeLauncherInputHost? _launcherInputHost;
    private readonly bool _visualInspectionMode;
#if NYKUR_EDGE_VISUAL_TEST
    private EdgeSide? _visualInspectionSide;
    private bool _nativeFrameReported;
#endif
    private DisplayInfo _display;
    private double _animationFrom;
    private double _animationTo;
    private double _progress;
    private bool _isSettingsInteractive;
    private bool _isPinnedInteractive;
    private bool _isExpandedBackdropApplied;
    private bool _isAcrylicTargetAttached;
    private bool _windowSubclassInstalled;
    private GCHandle _windowSubclassLifetime;
    private bool _canApplyAdaptiveRegion;
    private bool _windowRegionApplied;
    private int _regionWidth;
    private int _regionHeight;
    private EdgeSide _regionSide;
    private int _regionThickness;
    private int _regionProgressBucket = -1;
    private int _regionNotificationBucket = -1;
    private bool _isVisualWindowInputTransparent;
    private bool _isMainWindowShown;
    private double _notificationExpansion;
    private bool _disposed;

    public EdgeWindowController(
        Window window,
        IDisplayService displayService,
        SettingsService settings)
    {
        _window = window;
        _windowSubclassProcedure = OnWindowSubclassMessage;
        _displayService = displayService;
        _settings = settings;
        _appWindow = window.AppWindow;
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _expandedBackdrop = window.SystemBackdrop ?? new DesktopAcrylicBackdrop();
        window.SystemBackdrop = null;
        _backdropTarget = window.As<ICompositionSupportsSystemBackdrop>();
        _backdropDispatcherQueueController = EnsureCompositionDispatcherQueue();
        _backdropCompositor = new Windows.UI.Composition.Compositor();
        _collapsedBackdropBrush =
            _backdropCompositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        // A transparent XAML root alone is not sufficient: without an output
        // backdrop WinUI resolves clear pixels against its opaque theme surface.
        // The brush is attached in ShowWithoutActivation, after MainPage and its
        // Win2D surface have joined the XAML tree. Attaching it before the root
        // content exists can leave the transparent backdrop visible but starve
        // the foreground canvas on some composition paths.
        if (DesktopAcrylicController.IsSupported())
        {
            _backdropConfiguration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark,
            };
            _acrylicController = new DesktopAcrylicController();
        }
        _isExpandedBackdropApplied = false;
        _display = displayService.GetPrimaryDisplay();
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

        _nativeOverlay = NativeEdgeCompositionHost.TryCreate();
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.PointerEntered += OnNativeOverlayPointerEntered;
            _nativeOverlay.PointerExited += OnNativeOverlayPointerExited;
            _nativeOverlay.Clicked += OnNativeOverlayClicked;
            _nativeOverlay.SecondaryClicked += OnNativeOverlaySecondaryClicked;
        }
        else if (!_visualInspectionMode)
        {
            try
            {
                _launcherInputHost = new EdgeLauncherInputHost();
                _launcherInputHost.PointerEntered += OnLauncherPointerEntered;
                _launcherInputHost.PointerExited += OnLauncherPointerExited;
                _launcherInputHost.Clicked += OnLauncherClicked;
                _launcherInputHost.SecondaryClicked += OnLauncherSecondaryClicked;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // WM_NCHITTEST on the WinUI HWND remains the last-resort input
                // path if the small native launcher cannot be created.
                Debug.WriteLine($"Native Edge launcher input is unavailable: {exception}");
            }
        }

        ConfigurePresenter();
        ApplyNativeWindowStyles(noActivate: true);
        SuppressNativeFrame();
        InstallWindowSubclass();
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

    public bool SuppressesPointerPreview =>
        _visualInspectionMode && IsPassiveVisualInspection();

    public bool IsPointerOverInteractiveSurface()
    {
        if (!GetCursorPosition(out var point))
        {
            return false;
        }

        var hitWindow = WindowFromPoint(point);
        if (hitWindow == IntPtr.Zero)
        {
            return false;
        }

        var rootWindow = GetAncestor(hitWindow, GetAncestorRoot);
        if (rootWindow == IntPtr.Zero)
        {
            rootWindow = hitWindow;
        }

        if (rootWindow == _windowHandle)
        {
            // The rendering HWND can own more transparent geometry than the
            // interaction surface in either renderer path. Its bounds are not
            // its input bounds: only the orb/organic bloom is interactive.
            return IsFallbackInteractivePoint(point);
        }

        if (_nativeOverlay?.OwnsInteractiveWindow(rootWindow) ?? false)
        {
            return true;
        }

        return _launcherInputHost?.OwnsWindow(rootWindow) ?? false;
    }

    internal bool HasNativeCollapsedSurface => _nativeOverlay is not null;

    public void ShowWithoutActivation()
    {
        ThrowIfDisposed();
        ConfigureTransparentCompositionSurface();
        _backdropTarget.SystemBackdrop = _collapsedBackdropBrush;
        if (_visualInspectionMode && !IsPassiveVisualInspection())
        {
            ApplyNativeWindowStyles(noActivate: false);
            _appWindow.Show(activateWindow: true);
            _window.Activate();
            SuppressNativeFrame();
            _ = DispatcherQueue.GetForCurrentThread().TryEnqueue(
                DispatcherQueuePriority.Low,
                SuppressNativeFrame);
            _isMainWindowShown = true;
            UpdateCollapsedInteractionRouting();
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
        _isMainWindowShown = true;
        UpdateCollapsedInteractionRouting();
    }

    private static bool IsPassiveVisualInspection() =>
        string.Equals(
            Environment.GetEnvironmentVariable("NYKUR_EDGE_VISUAL_TEST_PASSIVE"),
            "1",
            StringComparison.Ordinal) ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "nykur-edge.visual-test"));

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
        var rendererStatus = _nativeOverlay is null
            ? $"fallback ({NativeEdgeCompositionHost.LastFailure ?? "unknown"})"
            : "native";
        _appWindow.Title = $"{status} · {rendererStatus}";
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
            _animationTo = target;
            UpdateCollapsedInteractionRouting();
            return;
        }

        _animationTimer.Stop();
        _animationFrom = _progress;
        _animationTo = target;
        _animationClock.Restart();
        // Stop the full-height visual HWND from being click-through before the
        // first opening frame. It is restored only after the final collapsed
        // frame, not while a closing animation is still in flight.
        UpdateCollapsedInteractionRouting();

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
        // Reserve the notification lens' maximum input/render region for the
        // whole visible response. The glass still animates continuously, but
        // its HWND clip no longer grows in quantized one-pixel steps.
        var reservedProgress = progress > 0 ? 1d : 0d;
        if (reservedProgress == _notificationExpansion)
        {
            return;
        }

        _notificationExpansion = reservedProgress;
        UpdateLauncherBounds();
        if (_canApplyAdaptiveRegion && _regionWidth > 0 && _regionHeight > 0)
        {
            ApplyWindowRegion(_regionWidth, _regionHeight, _progress);
        }
    }

    internal void RenderCollapsedEdge(EdgeFluidFrame frame)
    {
        if (_nativeOverlay is null || _disposed)
        {
            return;
        }

#if NYKUR_EDGE_VISUAL_TEST
        if (!_nativeFrameReported)
        {
            _appWindow.Title = $"{_appWindow.Title} · fed";
            _nativeFrameReported = true;
        }
#endif

        _nativeOverlay.Render(frame);
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
        if (_windowSubclassInstalled)
        {
            var removed = RemoveWindowSubclass(
                _windowHandle,
                _windowSubclassProcedure,
                WindowSubclassId);
            if (removed || !IsWindow(_windowHandle))
            {
                ReleaseWindowSubclassLifetime();
            }
        }
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.PointerEntered -= OnNativeOverlayPointerEntered;
            _nativeOverlay.PointerExited -= OnNativeOverlayPointerExited;
            _nativeOverlay.Clicked -= OnNativeOverlayClicked;
            _nativeOverlay.SecondaryClicked -= OnNativeOverlaySecondaryClicked;
            _nativeOverlay.Dispose();
        }
        if (_launcherInputHost is not null)
        {
            _launcherInputHost.PointerEntered -= OnLauncherPointerEntered;
            _launcherInputHost.PointerExited -= OnLauncherPointerExited;
            _launcherInputHost.Clicked -= OnLauncherClicked;
            _launcherInputHost.SecondaryClicked -= OnLauncherSecondaryClicked;
            _launcherInputHost.Dispose();
        }
        _acrylicController?.RemoveAllSystemBackdropTargets();
        _acrylicController?.Dispose();
        _backdropTarget.SystemBackdrop = null;
        _collapsedBackdropBrush.Dispose();
        _backdropCompositor.Dispose();
        if (_backdropDispatcherQueueController != IntPtr.Zero)
        {
            _ = Marshal.Release(_backdropDispatcherQueueController);
        }
        _window.SystemBackdrop = null;
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
        UpdateLauncherBounds();
        _appWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        var showExpandedBackdrop = expansionProgress > ExpandedBackdropStartProgress;
        if (_canApplyAdaptiveRegion && showExpandedBackdrop)
        {
            // On opening, constrain the HWND before attaching acrylic. Otherwise
            // the material can briefly composite through the previous full-height
            // transparent region and reveal a one-frame slab.
            ApplyWindowRegion(bounds.Width, bounds.Height, expansionProgress);
        }
        // The quartic opening reaches roughly 0.2 on its first render tick. A
        // later handoff leaves several visible frames for the traveling field to
        // recede into the orb before acrylic takes ownership of the compact bloom.
        SetBackdropVisible(showExpandedBackdrop);
        if (_canApplyAdaptiveRegion && !showExpandedBackdrop)
        {
            // On closing, detach acrylic first and only then restore the larger
            // transparent render allowance.
            ApplyWindowRegion(bounds.Width, bounds.Height, expansionProgress);
        }
        UpdateCollapsedInteractionRouting();
    }

    private void UpdateLauncherBounds()
    {
        _launcherInputHost?.UpdateBounds(
            _display.WorkArea,
            _display.Dpi,
            EffectiveSide,
            _notificationExpansion);
    }

    private void OnDisplayPollTick(DispatcherQueueTimer sender, object args)
    {
        var current = _displayService.GetPrimaryDisplay();
        if (current.WorkArea != _display.WorkArea || current.Dpi != _display.Dpi)
        {
            _display = current;
            ConfigureDwmTransparency();
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

        var isCollapsed = progress <= 0.001;
        var useNativeCollapsedSurface = _nativeOverlay is not null && isCollapsed;
        var useFullHeightFallbackSurface =
            _nativeOverlay is null && progress <= ExpandedBackdropStartProgress;
        if (useFullHeightFallbackSurface)
        {
            // The wave can now migrate and peak anywhere along the monitor's
            // vertical edge. A contour-shaped HRGN would either clip that
            // motion or need rebuilding every frame. Keep the rendering region
            // transparent and full-height; WM_NCHITTEST below supplies the
            // much smaller input region independently.
            UnionRegion(composite, CreateRectRegion(0, 0, width, height));
        }
        else if (!useNativeCollapsedSurface)
        {
            var fluidField = CreateFluidFieldRegion(
                width,
                height,
                side,
                scale,
                _notificationExpansion);
            UnionRegion(composite, fluidField);
        }

        var orbHalfWidth = Math.Max(
            1,
            (int)Math.Round((24 + (26 * _notificationExpansion)) * scale));
        var orbHalfHeight = Math.Max(
            1,
            (int)Math.Round((23 + (7 * _notificationExpansion)) * scale));
        var centerY = height / 2;
        if (useNativeCollapsedSurface)
        {
            // An entirely empty region causes WinUI/Win2D to stop producing
            // render callbacks, which also starves the independent native
            // Composition target of geometry frames. Retain one transparent
            // keep-alive pixel at the physical edge; the native HWND owns the
            // actual visual and interaction profile.
            var keepAliveX = side == EdgeSide.Right ? Math.Max(0, width - 1) : 0;
            var keepAlive = CreateRectRegion(keepAliveX, centerY, keepAliveX + 1, centerY + 1);
            UnionRegion(composite, keepAlive);
        }
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
        double scale,
        double notificationExpansion)
    {
        const int profilePointCount = 73;
        var points = new NativePoint[profilePointCount + 2];
        var edgeX = side == EdgeSide.Right ? width : 0;
        points[0] = new NativePoint(edgeX, 0);

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
            // This is deliberately a conservative envelope rather than a copy
            // of the animated renderer. The current field contains fine lanes
            // at several depths, broad low-alpha bloom strokes, playing-state
            // displacement, and a travelling notification pulse. An annular
            // region based on one old contour clipped most of those layers and
            // could make the entire app appear missing. Keep the desktop-facing
            // side dynamic-looking while allowing every renderer layer between
            // it and the physical edge to survive DWM clipping.
            var centerReachDip = 87 * centerEnvelope;
            var shoulderReachDip = 15 * orbShoulders;
            var distantReachDip = 10 * distantFlow;
            var notificationReachDip =
                20 * Math.Clamp(notificationExpansion, 0, 1) *
                Gaussian(normalizedY, 0.5, 4.2);
            var outerReachDip = presence *
                                (7 + centerReachDip + shoulderReachDip + distantReachDip) *
                                (1 - (orbChannel * 0.08)) +
                                notificationReachDip +
                                10;
            var outerReach = Math.Clamp(
                (int)Math.Ceiling(outerReachDip * scale),
                1,
                width);
            var y = Math.Clamp((int)Math.Round(normalizedY * (height - 1)), 0, height - 1);

            points[index + 1] = new NativePoint(
                side == EdgeSide.Right ? width - outerReach : outerReach,
                y);
        }

        points[^1] = new NativePoint(edgeX, height);
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
        UpdateCollapsedInteractionRouting();
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

    private void UpdateCollapsedInteractionRouting()
    {
        var launcherOwnsCollapsedInput =
            _launcherInputHost is not null &&
            _isMainWindowShown &&
            _progress <= 0.001 &&
            _animationTo <= 0.001 &&
            !_isPinnedInteractive &&
            !_isSettingsInteractive;

        SetVisualWindowInputTransparent(launcherOwnsCollapsedInput);
        if (_launcherInputHost is null)
        {
            return;
        }

        if (_isMainWindowShown)
        {
            if (launcherOwnsCollapsedInput)
            {
                _launcherInputHost.ShowWithoutActivation();
            }
            else
            {
                // Once hover starts the bloom, the WinUI launcher button owns
                // the same tightly bounded orb footprint. Retire the native target so
                // it cannot intercept panel/settings input while expanded.
                _launcherInputHost.Hide();
            }
        }
    }

    private void SetVisualWindowInputTransparent(bool transparent)
    {
        if (_isVisualWindowInputTransparent == transparent)
        {
            return;
        }

        var style = GetWindowLongPointer(_windowHandle, ExtendedStyleIndex).ToInt64();
        style = transparent
            ? style | ExtendedStyleTransparent
            : style & ~ExtendedStyleTransparent;
        _ = SetWindowLongPointer(_windowHandle, ExtendedStyleIndex, new IntPtr(style));
        _isVisualWindowInputTransparent = transparent;
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

    private void SetBackdropVisible(bool visible)
    {
        if (_isExpandedBackdropApplied == visible)
        {
            return;
        }

        if (_acrylicController is not null)
        {
            if (visible)
            {
                // The acrylic controller and Window.SystemBackdrop both target
                // the same root. Disconnect the transparent brush before the
                // expanded material takes ownership of that target.
                _backdropTarget.SystemBackdrop = null;

                if (!_isAcrylicTargetAttached)
                {
                    _acrylicController.AddSystemBackdropTarget(
                        _window.As<ICompositionSupportsSystemBackdrop>());
                    if (_backdropConfiguration is not null)
                    {
                        _acrylicController.SetSystemBackdropConfiguration(_backdropConfiguration);
                    }
                    _isAcrylicTargetAttached = true;
                }

                ConfigureAcrylicMaterial(expanded: true);
            }
            else if (_isAcrylicTargetAttached)
            {
                // A zero-opacity acrylic target can still composite a dark
                // fallback silhouette on transparent WinUI windows. Detach it
                // completely so collapsed mode contains only Win2D pixels.
                _acrylicController.RemoveAllSystemBackdropTargets();
                _isAcrylicTargetAttached = false;
            }

            if (!visible)
            {
                _backdropTarget.SystemBackdrop = _collapsedBackdropBrush;
            }
        }
        else if (_expandedBackdrop is not null)
        {
            if (visible)
            {
                _backdropTarget.SystemBackdrop = null;
                _window.SystemBackdrop = _expandedBackdrop;
            }
            else
            {
                _window.SystemBackdrop = null;
                _backdropTarget.SystemBackdrop = _collapsedBackdropBrush;
            }
        }
        _isExpandedBackdropApplied = visible;
    }

    private void ConfigureTransparentCompositionSurface()
    {
        ConfigureDwmTransparency();
        ClearTransparentBackingSurface(GetDeviceContext(_windowHandle), releaseDeviceContext: true);
    }

    private void ConfigureDwmTransparency()
    {
        var margins = new NativeMargins();
        _ = DwmExtendFrameIntoClientArea(_windowHandle, ref margins);

        var blurRegion = CreateRectRegion(-2, -2, -1, -1);
        if (blurRegion != IntPtr.Zero)
        {
            try
            {
                var blur = new DwmBlurBehind
                {
                    Flags = DwmBlurBehindEnable | DwmBlurBehindRegion,
                    IsEnabled = true,
                    BlurRegion = blurRegion,
                };
                _ = DwmEnableBlurBehindWindow(_windowHandle, ref blur);
            }
            finally
            {
                _ = DeleteObject(blurRegion);
            }
        }
    }

    private void ClearTransparentBackingSurface(
        IntPtr deviceContext,
        bool releaseDeviceContext)
    {
        if (deviceContext == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (!GetClientRectangle(_windowHandle, out var clientRectangle))
            {
                return;
            }

            var backgroundBrush = CreateSolidBrush(0);
            if (backgroundBrush == IntPtr.Zero)
            {
                return;
            }

            try
            {
                _ = FillRectangle(deviceContext, ref clientRectangle, backgroundBrush);
            }
            finally
            {
                _ = DeleteObject(backgroundBrush);
            }
        }
        finally
        {
            if (releaseDeviceContext)
            {
                _ = ReleaseDeviceContext(_windowHandle, deviceContext);
            }
        }
    }

    private void InstallWindowSubclass()
    {
        _windowSubclassInstalled = SetWindowSubclass(
            _windowHandle,
            _windowSubclassProcedure,
            WindowSubclassId,
            UIntPtr.Zero);
        if (_windowSubclassInstalled)
        {
            // Keep the managed callback owner alive until comctl32 confirms the
            // subclass is gone. A failed removal must never leave a collectible
            // delegate behind an otherwise valid HWND.
            _windowSubclassLifetime = GCHandle.Alloc(this);
        }
    }

    private IntPtr OnWindowSubclassMessage(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == WindowMessageNonClientDestroy)
        {
            _ = RemoveWindowSubclass(
                windowHandle,
                _windowSubclassProcedure,
                WindowSubclassId);
            ReleaseWindowSubclassLifetime();
            return DefSubclassProc(windowHandle, message, wParam, lParam);
        }

        if (!_disposed)
        {
            if (message == WindowMessageNcHitTest)
            {
                return new IntPtr(
                    IsFallbackInteractivePoint(lParam)
                        ? HitTestClient
                        : HitTestTransparent);
            }
            if (message == WindowMessageDwmCompositionChanged)
            {
                ConfigureDwmTransparency();
            }
            else if (message == WindowMessageEraseBackground)
            {
                // Prevent WinUI's default theme brush from repainting the clear
                // composition surface after resize/display/DWM transitions.
                ClearTransparentBackingSurface(wParam, releaseDeviceContext: false);
                return new IntPtr(1);
            }
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private bool IsFallbackInteractivePoint(IntPtr packedScreenPoint)
    {
        var packed = packedScreenPoint.ToInt64();
        return IsFallbackInteractivePoint(new NativePoint(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF))));
    }

    private bool IsFallbackInteractivePoint(NativePoint screenPoint)
    {
        var clientPoint = screenPoint;
        if (!ScreenToClient(_windowHandle, ref clientPoint) ||
            !GetClientRectangle(_windowHandle, out var clientRectangle))
        {
            return false;
        }

        var width = Math.Max(1, clientRectangle.Right - clientRectangle.Left);
        var height = Math.Max(1, clientRectangle.Bottom - clientRectangle.Top);
        var scale = _display.Dpi > 0 ? _display.Dpi / 96d : 1d;
        var distanceFromEdge = EffectiveSide == EdgeSide.Right
            ? width - clientPoint.X
            : clientPoint.X;
        var distanceFromCenter = clientPoint.Y - (height / 2d);

        // Slightly pad the visible glass lens for comfortable hover targeting,
        // while keeping the rest of the full-height wave click-through. The
        // notification lens grows mainly inward, so its launcher envelope does
        // the same without turning the edge into a sidebar-sized input zone.
        var horizontalRadius =
            (EdgeLauncherInputHost.HorizontalRadiusDip +
             (EdgeLauncherInputHost.NotificationHorizontalGrowthDip * _notificationExpansion)) * scale;
        var verticalRadius =
            (EdgeLauncherInputHost.VerticalRadiusDip +
             (EdgeLauncherInputHost.NotificationVerticalGrowthDip * _notificationExpansion)) * scale;
        if (distanceFromEdge >= 0 && distanceFromEdge <= horizontalRadius)
        {
            var normalizedX = distanceFromEdge / horizontalRadius;
            var normalizedY = distanceFromCenter / verticalRadius;
            if ((normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1)
            {
                return true;
            }
        }

        if (_progress <= 0.001 || distanceFromEdge < 0)
        {
            return false;
        }

        // Match the organic panel bloom closely enough that controls remain
        // reliable throughout the staged opening, without letting the narrow
        // decorative wave field claim input above or below the real surface.
        var eased = 1 - Math.Pow(1 - Math.Clamp(_progress, 0, 1), 3);
        var orbRadius = 22 * scale;
        var targetHeight = Math.Min(height, EdgeWindowLayout.ExpandedHeightDip * scale * 0.86);
        var bloomHeight = orbRadius * 2 +
                          ((targetHeight - (orbRadius * 2)) * Math.Pow(eased, 0.72));
        var top = (height - bloomHeight) / 2d;
        var normalized = (clientPoint.Y - top) / Math.Max(1, bloomHeight);
        if (normalized is < 0 or > 1)
        {
            return false;
        }

        var edgeDistance = Math.Abs((normalized * 2) - 1);
        const double shoulderStart = 0.76;
        var shoulderProgress = Math.Clamp(
            (edgeDistance - shoulderStart) / (1 - shoulderStart),
            0,
            1);
        var capsule = edgeDistance <= shoulderStart
            ? 1
            : Math.Sqrt(Math.Max(0, 1 - (shoulderProgress * shoulderProgress)));
        var shoulder = Gaussian(normalized, 0.5, 2.1);
        var maximumReach = Math.Max(orbRadius, width - Math.Round(7 * scale));
        var panelReach = orbRadius +
                         ((maximumReach - orbRadius) * capsule *
                          (0.94 + (shoulder * 0.06)));
        return distanceFromEdge <= panelReach;
    }

    private void ReleaseWindowSubclassLifetime()
    {
        _windowSubclassInstalled = false;
        if (_windowSubclassLifetime.IsAllocated)
        {
            _windowSubclassLifetime.Free();
        }
    }

    private static IntPtr EnsureCompositionDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
        {
            return IntPtr.Zero;
        }

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2, // DQTYPE_THREAD_CURRENT
            ApartmentType = 2, // DQTAT_COM_STA
        };
        Marshal.ThrowExceptionForHR(
            CreateDispatcherQueueController(options, out var controller));
        return controller;
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

        // ResetProperties restores an opaque luminosity/fallback material.
        // Zero every contributor explicitly so transparent Win2D pixels reveal
        // the desktop instead of an acrylic-colored region silhouette.
        _acrylicController.TintColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        _acrylicController.TintOpacity = 0f;
        _acrylicController.LuminosityOpacity = 0f;
        _acrylicController.FallbackColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
    }

    private void OnNativeOverlayPointerEntered(object? sender, EventArgs args) =>
        CollapsedPointerEntered?.Invoke(this, EventArgs.Empty);

    private void OnNativeOverlayPointerExited(object? sender, EventArgs args) =>
        CollapsedPointerExited?.Invoke(this, EventArgs.Empty);

    private void OnNativeOverlayClicked(object? sender, EventArgs args) =>
        CollapsedClicked?.Invoke(this, EventArgs.Empty);

    private void OnNativeOverlaySecondaryClicked(object? sender, EventArgs args) =>
        CollapsedSecondaryClicked?.Invoke(this, EventArgs.Empty);

    private void OnLauncherPointerEntered(object? sender, EventArgs args) =>
        CollapsedPointerEntered?.Invoke(this, EventArgs.Empty);

    private void OnLauncherPointerExited(object? sender, EventArgs args) =>
        CollapsedPointerExited?.Invoke(this, EventArgs.Empty);

    private void OnLauncherClicked(object? sender, EventArgs args) =>
        CollapsedClicked?.Invoke(this, EventArgs.Empty);

    private void OnLauncherSecondaryClicked(object? sender, EventArgs args) =>
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

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPosition(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(
        IntPtr windowHandle,
        ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr windowHandle);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr windowHandle,
        WindowSubclassProcedure subclassProcedure,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr windowHandle,
        WindowSubclassProcedure subclassProcedure,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(
        IntPtr windowHandle,
        ref NativeMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(
        IntPtr windowHandle,
        ref DwmBlurBehind blurBehind);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr GetDeviceContext(IntPtr windowHandle);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDeviceContext(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRectangle(
        IntPtr windowHandle,
        out NativeRectangle rectangle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colorReference);

    [DllImport("user32.dll", EntryPoint = "FillRect")]
    private static extern int FillRectangle(
        IntPtr deviceContext,
        ref NativeRectangle rectangle,
        IntPtr brush);

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        DispatcherQueueOptions options,
        out IntPtr dispatcherQueueController);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind
    {
        public uint Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool IsEnabled;

        public IntPtr BlurRegion;

        [MarshalAs(UnmanagedType.Bool)]
        public bool TransitionOnMaximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    private delegate IntPtr WindowSubclassProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);
}
