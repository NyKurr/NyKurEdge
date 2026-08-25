using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;
using WinRT;
using Windows.UI;
using WUC = Windows.UI.Composition;
using WUCD = Windows.UI.Composition.Desktop;

// CsWinRT projects WinRT vector Append as a mutating instance method. The
// analyzer can misidentify it as LINQ's side-effect-free Append extension.
#pragma warning disable CA1806

namespace NyKurEdge.App.Presentation.Edge;

/// <summary>
/// Hosts the collapsed Edge in a standalone Windows Composition target. Unlike
/// a WinUI top-level surface, this HWND preserves per-pixel alpha, so transparent
/// wave pixels reveal the desktop rather than an opaque theme background.
/// </summary>
internal sealed class NativeEdgeCompositionHost : IDisposable
{
    private const int WindowLongUserData = -21;
    private const uint WindowStylePopup = 0x80000000;
    private const uint ExtendedStyleTopMost = 0x00000008;
    private const uint ExtendedStyleToolWindow = 0x00000080;
    private const uint ExtendedStyleNoRedirectionBitmap = 0x00200000;
    private const uint ExtendedStyleNoActivate = 0x08000000;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const int ShowWindowHide = 0;
    private const int ShowWindowNoActivate = 4;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessageNcCreate = 0x0081;
    private const uint WindowMessageNcDestroy = 0x0082;
    private const uint WindowMessageNcHitTest = 0x0084;
    private const uint WindowMessageMouseMove = 0x0200;
    private const uint WindowMessageLeftButtonUp = 0x0202;
    private const uint WindowMessageRightButtonUp = 0x0205;
    private const uint WindowMessageMouseLeave = 0x02A3;
    private const int HitTestClient = 1;
    private const int HitTestTransparent = -1;
    private const uint TrackMouseLeave = 0x00000002;
    private const int ErrorClassAlreadyExists = 1410;

    private static readonly IntPtr TopMostWindow = new(-1);
    private static readonly WindowProcedureDelegate WindowProcedure = StaticWindowProcedure;
    private static readonly object RegistrationGate = new();
    private static bool _windowClassRegistered;

    private readonly CanvasDevice _canvasDevice;
    private readonly WUC.Compositor _compositor;
    private readonly WUCD.DesktopWindowTarget _target;
    private readonly WUC.ShapeVisual _shapeVisual;
    private readonly WUC.CompositionPathGeometry _primaryPath;
    private readonly WUC.CompositionPathGeometry _secondaryPath;
    private readonly WUC.CompositionPathGeometry _interferencePath;
    private readonly WUC.CompositionPathGeometry _filamentPath;
    private readonly WUC.CompositionPathGeometry _lensPath;
    private readonly WUC.CompositionPathGeometry _lensOutlinePath;
    private readonly WUC.CompositionEllipseGeometry _orbNeutralGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbAccentGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbHighlightGeometry;
    private readonly WUC.CompositionColorBrush _lensDarkBrush;
    private readonly WUC.CompositionColorBrush _lensAccentBrush;
    private readonly WUC.CompositionColorBrush _lensRimBrush;
    private readonly WUC.CompositionColorBrush _orbNeutralBrush;
    private readonly WUC.CompositionColorBrush _orbAccentBrush;
    private readonly WUC.CompositionColorBrush _orbHighlightBrush;
    private readonly List<VerticalGradientBinding> _accentGradients = [];
    private readonly List<CanvasGeometry> _currentGeometries = [];
    private readonly GCHandle _selfHandle;
    private readonly IntPtr _windowHandle;
    private double _dpiScale = 1;
    private float _widthDip = 1;
    private float _heightDip = 1;
    private double _expansionProgress;
    private Color _lastAccent;
    private bool _hasAccent;
    private bool _isShown;
    private bool _pointerInside;
    private bool _disposed;
    private EdgeSide _side = EdgeSide.Right;

    private NativeEdgeCompositionHost()
    {
        EnsureWindowClass();
        _selfHandle = GCHandle.Alloc(this);
        _windowHandle = CreateWindowEx(
            ExtendedStyleTopMost |
            ExtendedStyleToolWindow |
            ExtendedStyleNoActivate |
            ExtendedStyleNoRedirectionBitmap,
            WindowClassName,
            string.Empty,
            WindowStylePopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            GCHandle.ToIntPtr(_selfHandle));
        if (_windowHandle == IntPtr.Zero)
        {
            _selfHandle.Free();
            throw new InvalidOperationException(
                $"Could not create the native Edge composition window ({Marshal.GetLastWin32Error()}).");
        }

        _canvasDevice = CanvasDevice.GetSharedDevice();
        _compositor = new WUC.Compositor();
        var interop = _compositor.As<ICompositorDesktopInterop>();
        Marshal.ThrowExceptionForHR(
            interop.CreateDesktopWindowTarget(_windowHandle, isTopmost: true, out var targetPointer));
        try
        {
            _target = WUCD.DesktopWindowTarget.FromAbi(targetPointer);
        }
        finally
        {
            if (targetPointer != IntPtr.Zero)
            {
                _ = Marshal.Release(targetPointer);
            }
        }

        _shapeVisual = _compositor.CreateShapeVisual();
        _target.Root = _shapeVisual;

        _primaryPath = _compositor.CreatePathGeometry();
        _secondaryPath = _compositor.CreatePathGeometry();
        _interferencePath = _compositor.CreatePathGeometry();
        _filamentPath = _compositor.CreatePathGeometry();
        _lensPath = _compositor.CreatePathGeometry();
        _lensOutlinePath = _compositor.CreatePathGeometry();

        AddGradientStroke(_primaryPath, 12f, 5, neutral: false);
        AddGradientStroke(_primaryPath, 6f, 11, neutral: false);
        AddGradientStroke(_primaryPath, 2.8f, 20, neutral: false);
        AddGradientStroke(_interferencePath, 0.78f, 30, neutral: false);
        AddGradientStroke(_secondaryPath, 1.02f, 48, neutral: false);
        AddGradientStroke(_primaryPath, 1.55f, 142, neutral: false);
        AddGradientStroke(_primaryPath, 0.48f, 38, neutral: true);
        AddGradientStroke(_filamentPath, 0.68f, 34, neutral: true);

        AddSolidStroke(_lensOutlinePath, 12f, Color.FromArgb(14, 120, 180, 220));
        AddSolidStroke(_lensOutlinePath, 4.2f, Color.FromArgb(25, 120, 180, 220));

        _lensDarkBrush = _compositor.CreateColorBrush(Color.FromArgb(43, 8, 12, 18));
        _lensAccentBrush = _compositor.CreateColorBrush(Color.FromArgb(15, 120, 180, 220));
        AddFill(_lensPath, _lensDarkBrush);
        AddFill(_lensPath, _lensAccentBrush);

        _lensRimBrush = _compositor.CreateColorBrush(Color.FromArgb(94, 239, 244, 248));
        AddSolidStroke(_lensOutlinePath, 0.76f, _lensRimBrush);
        AddGradientStroke(_lensOutlinePath, 1.35f, 58, neutral: false);

        _orbNeutralGeometry = _compositor.CreateEllipseGeometry();
        _orbAccentGeometry = _compositor.CreateEllipseGeometry();
        _orbHighlightGeometry = _compositor.CreateEllipseGeometry();
        _orbNeutralBrush = _compositor.CreateColorBrush(Color.FromArgb(11, 246, 249, 252));
        _orbAccentBrush = _compositor.CreateColorBrush(Color.FromArgb(22, 120, 180, 220));
        _orbHighlightBrush = _compositor.CreateColorBrush(Color.FromArgb(190, 250, 252, 255));
        AddFill(_orbNeutralGeometry, _orbNeutralBrush);
        AddFill(_orbAccentGeometry, _orbAccentBrush);
        AddFill(_orbHighlightGeometry, _orbHighlightBrush);
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerExited;

    public event EventHandler? Clicked;

    public event EventHandler? SecondaryClicked;

    public static NativeEdgeCompositionHost? TryCreate()
    {
        try
        {
            return new NativeEdgeCompositionHost();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Native collapsed Edge composition is unavailable; using WinUI fallback: {exception}");
            return null;
        }
    }

    public void UpdateBounds(EdgeWindowBounds bounds, uint dpi, EdgeSide side)
    {
        ThrowIfDisposed();
        _side = side;
        _dpiScale = dpi > 0 ? dpi / 96d : 1d;
        _widthDip = (float)(bounds.Width / _dpiScale);
        _heightDip = (float)(bounds.Height / _dpiScale);
        _shapeVisual.Size = new Vector2(_widthDip, _heightDip);
        _shapeVisual.Scale = new Vector3((float)_dpiScale, (float)_dpiScale, 1);
        _ = SetWindowPos(
            _windowHandle,
            TopMostWindow,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            SetWindowPositionNoActivate | SetWindowPositionShowWindow);
        UpdateVisibility();
    }

    public void SetExpansionProgress(double progress)
    {
        ThrowIfDisposed();
        _expansionProgress = Math.Clamp(progress, 0, 1);
        var fade = 1 - SmootherStep(Math.Clamp(_expansionProgress / 0.38, 0, 1));
        _shapeVisual.Opacity = (float)fade;
        UpdateVisibility();
    }

    public void Render(
        Vector2[] primary,
        Vector2[] secondary,
        Vector2[] interference,
        Vector2[] filament,
        Color accent,
        EdgeOrbScale orbScale,
        double notificationProgress,
        float energy)
    {
        ThrowIfDisposed();
        if (_expansionProgress > 0.001 || !_isShown)
        {
            return;
        }

        var baseHeight = orbScale == EdgeOrbScale.Small ? 31f : 38f;
        var baseReach = orbScale == EdgeOrbScale.Small ? 16f : 21f;
        var visibleReach = baseReach + ((float)notificationProgress * 27f);
        var lensHeight = baseHeight + ((float)notificationProgress * 12f);
        var primaryGeometry = CreateSmoothPath(primary);
        var secondaryGeometry = CreateSmoothPath(secondary);
        var interferenceGeometry = CreateSmoothPath(interference);
        var filamentGeometry = CreateSmoothPath(filament);
        var lensGeometry = CreateLensGeometry(visibleReach, lensHeight, closeAtEdge: true);
        var lensOutlineGeometry = CreateLensGeometry(visibleReach, lensHeight, closeAtEdge: false);

        _primaryPath.Path = new WUC.CompositionPath(primaryGeometry);
        _secondaryPath.Path = new WUC.CompositionPath(secondaryGeometry);
        _interferencePath.Path = new WUC.CompositionPath(interferenceGeometry);
        _filamentPath.Path = new WUC.CompositionPath(filamentGeometry);
        _lensPath.Path = new WUC.CompositionPath(lensGeometry);
        _lensOutlinePath.Path = new WUC.CompositionPath(lensOutlineGeometry);

        foreach (var geometry in _currentGeometries)
        {
            geometry.Dispose();
        }
        _currentGeometries.Clear();
        _currentGeometries.Add(primaryGeometry);
        _currentGeometries.Add(secondaryGeometry);
        _currentGeometries.Add(interferenceGeometry);
        _currentGeometries.Add(filamentGeometry);
        _currentGeometries.Add(lensGeometry);
        _currentGeometries.Add(lensOutlineGeometry);

        if (!_hasAccent || !_lastAccent.Equals(accent))
        {
            _lastAccent = accent;
            _hasAccent = true;
            foreach (var gradient in _accentGradients)
            {
                gradient.Update(accent);
            }
            _lensAccentBrush.Color = Color.FromArgb(15, accent.R, accent.G, accent.B);
        }
        _orbAccentBrush.Color = Color.FromArgb(
            (byte)(18 + (Math.Clamp(energy, 0, 1) * 11)),
            accent.R,
            accent.G,
            accent.B);

        var edgeX = _side == EdgeSide.Right ? _widthDip : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var centerY = _heightDip / 2f;
        var radius = lensHeight * 0.37f;
        _orbNeutralGeometry.Center = new Vector2(edgeX, centerY);
        _orbNeutralGeometry.Radius = new Vector2(radius, radius);
        _orbAccentGeometry.Center = new Vector2(edgeX, centerY);
        _orbAccentGeometry.Radius = new Vector2(radius * 0.82f, radius * 0.82f);
        _orbHighlightGeometry.Center = new Vector2(
            edgeX + (direction * visibleReach * 0.73f),
            centerY - (lensHeight * 0.24f));
        var highlightRadius = Math.Max(0.8f, visibleReach * 0.062f);
        _orbHighlightGeometry.Radius = new Vector2(highlightRadius, highlightRadius);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = ShowWindow(_windowHandle, ShowWindowHide);
        _target.Root = null;
        foreach (var geometry in _currentGeometries)
        {
            geometry.Dispose();
        }
        _currentGeometries.Clear();
        _ = DestroyWindow(_windowHandle);
        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
        _compositor.Dispose();
    }

    private void AddGradientStroke(
        WUC.CompositionGeometry geometry,
        float thickness,
        byte peakAlpha,
        bool neutral)
    {
        var binding = new VerticalGradientBinding(_compositor, peakAlpha, neutral);
        _accentGradients.Add(binding);
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = binding.Brush;
        shape.StrokeThickness = thickness;
        shape.StrokeStartCap = WUC.CompositionStrokeCap.Round;
        shape.StrokeEndCap = WUC.CompositionStrokeCap.Round;
        shape.StrokeLineJoin = WUC.CompositionStrokeLineJoin.Round;
        _shapeVisual.Shapes.Append(shape);
    }

    private void AddSolidStroke(
        WUC.CompositionGeometry geometry,
        float thickness,
        Color color) =>
        AddSolidStroke(geometry, thickness, _compositor.CreateColorBrush(color));

    private void AddSolidStroke(
        WUC.CompositionGeometry geometry,
        float thickness,
        WUC.CompositionColorBrush brush)
    {
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = thickness;
        shape.StrokeStartCap = WUC.CompositionStrokeCap.Round;
        shape.StrokeEndCap = WUC.CompositionStrokeCap.Round;
        shape.StrokeLineJoin = WUC.CompositionStrokeLineJoin.Round;
        _shapeVisual.Shapes.Append(shape);
    }

    private void AddFill(WUC.CompositionGeometry geometry, WUC.CompositionBrush brush)
    {
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        _shapeVisual.Shapes.Append(shape);
    }

    private CanvasGeometry CreateSmoothPath(Vector2[] points)
    {
        using var builder = new CanvasPathBuilder(_canvasDevice);
        builder.BeginFigure(points[0]);
        for (var index = 0; index < points.Length - 1; index++)
        {
            var previous = points[Math.Max(0, index - 1)];
            var current = points[index];
            var next = points[index + 1];
            var following = points[Math.Min(points.Length - 1, index + 2)];
            var controlOne = current + ((next - previous) / 6f);
            var controlTwo = next - ((following - current) / 6f);
            builder.AddCubicBezier(controlOne, controlTwo, next);
        }
        builder.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }

    private CanvasGeometry CreateLensGeometry(
        float visibleReach,
        float lensHeight,
        bool closeAtEdge)
    {
        var edgeX = _side == EdgeSide.Right ? _widthDip : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var centerY = _heightDip / 2f;
        var innerX = edgeX + (direction * visibleReach);
        var top = centerY - (lensHeight / 2f);
        var bottom = centerY + (lensHeight / 2f);
        var radius = lensHeight / 2f;
        const float ellipseKappa = 0.5522848f;

        using var builder = new CanvasPathBuilder(_canvasDevice);
        builder.BeginFigure(new Vector2(edgeX, top));
        builder.AddCubicBezier(
            new Vector2(edgeX + (direction * visibleReach * ellipseKappa), top),
            new Vector2(innerX, centerY - (radius * ellipseKappa)),
            new Vector2(innerX, centerY));
        builder.AddCubicBezier(
            new Vector2(innerX, centerY + (radius * ellipseKappa)),
            new Vector2(edgeX + (direction * visibleReach * ellipseKappa), bottom),
            new Vector2(edgeX, bottom));
        builder.EndFigure(closeAtEdge ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }

    private void UpdateVisibility()
    {
        // Keep the transparent anchor window alive while the WinUI bloom is
        // open. It remains hit-test transparent everywhere except the visible
        // wave/orb profile, making the embedded launcher reliable on both
        // sides without blocking the panel beneath it.
        const bool shouldShow = true;
        if (_isShown == shouldShow)
        {
            return;
        }
        _isShown = shouldShow;
        _ = ShowWindow(_windowHandle, shouldShow ? ShowWindowNoActivate : ShowWindowHide);
    }

    private IntPtr HandleWindowMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WindowMessageEraseBackground:
                return new IntPtr(1);
            case WindowMessageNcHitTest:
                return new IntPtr(IsInteractivePoint(lParam) ? HitTestClient : HitTestTransparent);
            case WindowMessageMouseMove:
                if (!_pointerInside)
                {
                    _pointerInside = true;
                    var tracking = new TrackMouseEventData
                    {
                        Size = (uint)Marshal.SizeOf<TrackMouseEventData>(),
                        Flags = TrackMouseLeave,
                        WindowHandle = _windowHandle,
                    };
                    _ = TrackMouseEvent(ref tracking);
                    PointerEntered?.Invoke(this, EventArgs.Empty);
                }
                return IntPtr.Zero;
            case WindowMessageMouseLeave:
                _pointerInside = false;
                PointerExited?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            case WindowMessageLeftButtonUp:
                Clicked?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            case WindowMessageRightButtonUp:
                SecondaryClicked?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            default:
                return DefWindowProc(_windowHandle, message, wParam, lParam);
        }
    }

    private bool IsInteractivePoint(IntPtr packedScreenPoint)
    {
        var packed = packedScreenPoint.ToInt64();
        var point = new NativePoint(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF)));
        if (!ScreenToClient(_windowHandle, ref point))
        {
            return false;
        }

        var xDip = point.X / _dpiScale;
        var yDip = point.Y / _dpiScale;
        var normalized = yDip / Math.Max(1, _heightDip);
        if (normalized is < 0 or > 1)
        {
            return false;
        }

        var distanceFromEdge = _side == EdgeSide.Right ? _widthDip - xDip : xDip;
        var orbDx = distanceFromEdge / 27d;
        var orbDy = (yDip - (_heightDip / 2d)) / 29d;
        if ((orbDx * orbDx) + (orbDy * orbDy) <= 1)
        {
            return true;
        }

        var profile = FluidProfileReach(normalized);
        return VerticalPresence(normalized) > 0.035 &&
               Math.Abs(distanceFromEdge - profile) <= 12;
    }

    private static double FluidProfileReach(double normalized)
    {
        var presence = VerticalPresence(normalized);
        var center = Gaussian(normalized, 0.5, 2.05);
        var orbChannel = Gaussian(normalized, 0.5, 15.8);
        var shoulders =
            Gaussian(normalized, 0.435, 20.5) +
            Gaussian(normalized, 0.565, 20.5);
        var distantFlow =
            Gaussian(normalized, 0.285, 9.6) +
            Gaussian(normalized, 0.715, 9.6);
        return presence *
               (2.4 + (21.5 * center) + (4.8 * shoulders) + (2.8 * distantFlow)) *
               (1 - (orbChannel * 0.36));
    }

    private static double VerticalPresence(double normalized)
    {
        var edgeFade = SmootherStep(Math.Clamp(normalized / 0.105, 0, 1)) *
                       SmootherStep(Math.Clamp((1 - normalized) / 0.105, 0, 1));
        var center = Gaussian(normalized, 0.5, 2.05);
        return edgeFade * (0.17 + (0.83 * center));
    }

    private static double Gaussian(double value, double center, double sharpness) =>
        Math.Exp(-Math.Pow((value - center) * sharpness, 2));

    private static double SmootherStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * value * ((value * ((value * 6) - 15)) + 10);
    }

    private static void EnsureWindowClass()
    {
        lock (RegistrationGate)
        {
            if (_windowClassRegistered)
            {
                return;
            }

            var registration = new WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<WindowClassEx>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
                Instance = GetModuleHandle(null),
                Cursor = LoadCursor(IntPtr.Zero, new IntPtr(32512)),
                ClassName = WindowClassName,
            };
            if (RegisterClassEx(ref registration) == 0 &&
                Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                throw new InvalidOperationException(
                    $"Could not register the native Edge composition class ({Marshal.GetLastWin32Error()}).");
            }
            _windowClassRegistered = true;
        }
    }

    private static IntPtr StaticWindowProcedure(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam)
    {
        if (message == WindowMessageNcCreate)
        {
            var creation = Marshal.PtrToStructure<CreateStructure>(lParam);
            _ = SetWindowLongPointer(windowHandle, WindowLongUserData, creation.CreateParameters);
        }

        var userData = GetWindowLongPointer(windowHandle, WindowLongUserData);
        NativeEdgeCompositionHost? host = null;
        if (userData != IntPtr.Zero)
        {
            var handle = GCHandle.FromIntPtr(userData);
            host = handle.Target as NativeEdgeCompositionHost;
        }

        if (message == WindowMessageNcDestroy)
        {
            _ = SetWindowLongPointer(windowHandle, WindowLongUserData, IntPtr.Zero);
        }

        return host is null
            ? DefWindowProc(windowHandle, message, wParam, lParam)
            : host.HandleWindowMessage(message, wParam, lParam);
    }

    private static IntPtr GetWindowLongPointer(IntPtr windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new IntPtr(GetWindowLong32(windowHandle, index));

    private static IntPtr SetWindowLongPointer(IntPtr windowHandle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new IntPtr(SetWindowLong32(windowHandle, index, value.ToInt32()));

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private const string WindowClassName = "NyKurEdge.NativeCompositionHost";

    [ComImport]
    [Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        [PreserveSig]
        int CreateDesktopWindowTarget(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
            out IntPtr result);

        [PreserveSig]
        int EnsureOnThread(int threadId);
    }

    private sealed class VerticalGradientBinding
    {
        private static readonly float[] Offsets = [0f, 0.1f, 0.29f, 0.5f, 0.71f, 0.9f, 1f];
        private static readonly float[] Strengths = [0f, 0.08f, 0.42f, 1f, 0.42f, 0.08f, 0f];
        private readonly WUC.CompositionColorGradientStop[] _stops;
        private readonly byte _peakAlpha;
        private readonly bool _neutral;

        public VerticalGradientBinding(WUC.Compositor compositor, byte peakAlpha, bool neutral)
        {
            _peakAlpha = peakAlpha;
            _neutral = neutral;
            Brush = compositor.CreateLinearGradientBrush();
            Brush.MappingMode = WUC.CompositionMappingMode.Relative;
            Brush.StartPoint = Vector2.Zero;
            Brush.EndPoint = new Vector2(0, 1);
            _stops = new WUC.CompositionColorGradientStop[Offsets.Length];
            for (var index = 0; index < Offsets.Length; index++)
            {
                _stops[index] = compositor.CreateColorGradientStop();
                _stops[index].Offset = Offsets[index];
                Brush.ColorStops.Append(_stops[index]);
            }
            Update(Color.FromArgb(255, 120, 180, 220));
        }

        public WUC.CompositionLinearGradientBrush Brush { get; }

        public void Update(Color accent)
        {
            var red = _neutral ? (byte)246 : accent.R;
            var green = _neutral ? (byte)249 : accent.G;
            var blue = _neutral ? (byte)252 : accent.B;
            for (var index = 0; index < _stops.Length; index++)
            {
                var alpha = (byte)Math.Clamp(
                    (int)Math.Round(_peakAlpha * Strengths[index]),
                    0,
                    255);
                _stops[index].Color = Color.FromArgb(alpha, red, green, blue);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedureDelegate(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStructure
    {
        public IntPtr CreateParameters;
        public IntPtr Instance;
        public IntPtr Menu;
        public IntPtr Parent;
        public int Height;
        public int Width;
        public int Y;
        public int X;
        public int Style;
        public IntPtr Name;
        public IntPtr Class;
        public uint ExtendedStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr WindowHandle;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(double x, double y)
    {
        public int X = (int)Math.Round(x);
        public int Y = (int)Math.Round(y);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TrackMouseEventData eventData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr windowHandle, int index, IntPtr value);
}

#pragma warning restore CA1806
