using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
    private const string NativeCompositionOptInVariable = "NYKUR_EDGE_NATIVE_COMPOSITION";
    private const int OrbMeshStrandCount = 7;
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

    internal static string? LastFailure { get; private set; }

    private readonly CanvasDevice _canvasDevice;
    private readonly WUC.Compositor _compositor;
    private readonly WUCD.DesktopWindowTarget _target;
    private readonly WUC.ShapeVisual _shapeVisual;
    private readonly WUC.CompositionPathGeometry _primaryPath;
    private readonly WUC.CompositionPathGeometry _secondaryPath;
    private readonly WUC.CompositionPathGeometry _interferencePath;
    private readonly WUC.CompositionPathGeometry _filamentPath;
    private readonly WUC.CompositionPathGeometry[] _fineStrandPaths;
    private readonly WUC.CompositionPathGeometry _lensPath;
    private readonly WUC.CompositionPathGeometry _lensOutlinePath;
    private readonly WUC.CompositionPathGeometry _lensInnerPath;
    private readonly WUC.CompositionPathGeometry _lensCausticPath;
    private readonly WUC.CompositionEllipseGeometry _ambientGlowGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbBloomGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbDepthGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbNeutralGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbAccentGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbRefractionGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbHighlightGeometry;
    private readonly WUC.CompositionEllipseGeometry _orbSparkGeometry;
    private readonly WUC.CompositionEllipseGeometry[] _orbMeshGeometries;
    private readonly WUC.CompositionColorBrush _lensDarkBrush;
    private readonly WUC.CompositionColorBrush _lensAccentBrush;
    private readonly WUC.CompositionColorBrush _lensRimBrush;
    private readonly WUC.CompositionBrush _ambientGlowBrush;
    private readonly WUC.CompositionBrush _orbBloomBrush;
    private readonly RadialGradientBinding? _ambientGlow;
    private readonly RadialGradientBinding? _orbBloom;
    private readonly WUC.CompositionColorBrush? _ambientGlowFallbackBrush;
    private readonly WUC.CompositionColorBrush? _orbBloomFallbackBrush;
    private readonly WUC.CompositionColorBrush _orbDepthBrush;
    private readonly WUC.CompositionColorBrush _orbNeutralBrush;
    private readonly WUC.CompositionBrush _orbAccentBrush;
    private readonly RadialGradientBinding? _orbAccentGradient;
    private readonly WUC.CompositionColorBrush? _orbAccentFallbackBrush;
    private readonly WUC.CompositionColorBrush _orbRefractionBrush;
    private readonly WUC.CompositionColorBrush _orbHighlightBrush;
    private readonly WUC.CompositionColorBrush _orbSparkBrush;
    private readonly List<VerticalGradientBinding> _accentGradients = [];
    private readonly List<CanvasGeometry> _currentGeometries = [];
    private readonly List<CanvasGeometry> _pendingGeometries = [];
    private readonly GCHandle _selfHandle;
    private readonly IntPtr _windowHandle;
    private double _dpiScale = 1;
    private float _widthDip = 1;
    private float _heightDip = 1;
    private double _expansionProgress;
    private Color _lastAccent;
    private EdgePressureField _lastPressureField;
    private bool _hasAccent;
    private bool _isShown;
    private bool _pointerInside;
    private bool _disposed;
    private EdgeSide _side = EdgeSide.Right;

    private NativeEdgeCompositionHost()
    {
        EnsureWindowClass();
        _selfHandle = GCHandle.Alloc(this);
        var extendedStyle = ExtendedStyleTopMost |
                            ExtendedStyleToolWindow |
                            ExtendedStyleNoActivate;
#if !NYKUR_EDGE_VISUAL_TEST
        // Production uses a redirection-free target so only Composition pixels
        // exist on the desktop. QA keeps DWM redirection available because
        // Windows.Graphics.Capture otherwise cannot inspect this HWND.
        extendedStyle |= ExtendedStyleNoRedirectionBitmap;
#endif
        _windowHandle = CreateWindowEx(
            extendedStyle,
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
        _fineStrandPaths = new WUC.CompositionPathGeometry[EdgeWaveRenderer.FineStrandCount];
        for (var index = 0; index < _fineStrandPaths.Length; index++)
        {
            _fineStrandPaths[index] = _compositor.CreatePathGeometry();
        }
        _lensPath = _compositor.CreatePathGeometry();
        _lensOutlinePath = _compositor.CreatePathGeometry();
        _lensInnerPath = _compositor.CreatePathGeometry();
        _lensCausticPath = _compositor.CreatePathGeometry();

        _ambientGlowGeometry = _compositor.CreateEllipseGeometry();
        _orbBloomGeometry = _compositor.CreateEllipseGeometry();
        _orbDepthGeometry = _compositor.CreateEllipseGeometry();
        _orbNeutralGeometry = _compositor.CreateEllipseGeometry();
        _orbAccentGeometry = _compositor.CreateEllipseGeometry();
        _orbRefractionGeometry = _compositor.CreateEllipseGeometry();
        _orbHighlightGeometry = _compositor.CreateEllipseGeometry();
        _orbSparkGeometry = _compositor.CreateEllipseGeometry();
        _orbMeshGeometries = new WUC.CompositionEllipseGeometry[OrbMeshStrandCount];
        for (var index = 0; index < _orbMeshGeometries.Length; index++)
        {
            _orbMeshGeometries[index] = _compositor.CreateEllipseGeometry();
        }

        // Broad, extremely low-alpha optical volume. These are localized shapes,
        // never a rectangular backing surface.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            _ambientGlow = new RadialGradientBinding(_compositor, centerAlpha: 18, shoulderAlpha: 6);
            _orbBloom = new RadialGradientBinding(_compositor, centerAlpha: 27, shoulderAlpha: 9);
            _ambientGlowBrush = _ambientGlow.Brush;
            _orbBloomBrush = _orbBloom.Brush;
        }
        else
        {
            _ambientGlowFallbackBrush =
                _compositor.CreateColorBrush(Color.FromArgb(2, 120, 180, 220));
            _orbBloomFallbackBrush =
                _compositor.CreateColorBrush(Color.FromArgb(5, 120, 180, 220));
            _ambientGlowBrush = _ambientGlowFallbackBrush;
            _orbBloomBrush = _orbBloomFallbackBrush;
        }
        AddFill(_ambientGlowGeometry, _ambientGlowBrush);
        AddFill(_orbBloomGeometry, _orbBloomBrush);

        AddGradientStroke(_primaryPath, 34f, 1, neutral: false);
        AddGradientStroke(_primaryPath, 22f, 2, neutral: false);
        AddGradientStroke(_secondaryPath, 14f, 2, neutral: false);
        AddGradientStroke(_primaryPath, 8f, 4, neutral: false);
        AddGradientStroke(_primaryPath, 3.2f, 7, neutral: false);

        // The fine strand family carries the high-end filament character. A few
        // neutral strands act as refractive highlights without washing out the
        // artwork-derived accent.
        for (var index = 0; index < _fineStrandPaths.Length; index++)
        {
            var lane = index / (float)(_fineStrandPaths.Length - 1);
            var centerEmphasis = MathF.Sin(lane * MathF.PI);
            var opticalHighlight =
                index == _fineStrandPaths.Length / 3 ||
                index == (_fineStrandPaths.Length * 2) / 3;

            // The renderer deliberately uses a dense strand family. Keep each
            // individual strand quiet so their accumulation reads as airy glass
            // instead of a solid luminous strip. Only two lanes become neutral
            // optical catches; the artwork accent remains the dominant color.
            var peakAlpha = (byte)Math.Round(10 + (centerEmphasis * 30));
            var thickness = 0.28f + (centerEmphasis * 0.28f);
            AddGradientStroke(
                _fineStrandPaths[index],
                0.90f + (centerEmphasis * 0.75f),
                (byte)Math.Round(3 + (centerEmphasis * 6)),
                neutral: false);
            AddGradientStroke(
                _fineStrandPaths[index],
                thickness,
                peakAlpha,
                neutral: opticalHighlight);
        }

        AddGradientStroke(_interferencePath, 0.44f, 11, neutral: false);
        AddGradientStroke(_secondaryPath, 0.52f, 17, neutral: false);
        AddGradientStroke(_primaryPath, 0.56f, 19, neutral: false);
        AddGradientStroke(_primaryPath, 0.20f, 7, neutral: true);
        AddGradientStroke(_filamentPath, 0.36f, 15, neutral: true);

        AddGradientStroke(_lensOutlinePath, 12f, 13, neutral: false);
        AddGradientStroke(_lensOutlinePath, 4.2f, 24, neutral: false);

        _lensDarkBrush = _compositor.CreateColorBrush(Color.FromArgb(15, 8, 12, 18));
        _lensAccentBrush = _compositor.CreateColorBrush(Color.FromArgb(16, 120, 180, 220));
        AddFill(_lensPath, _lensDarkBrush);
        AddFill(_lensPath, _lensAccentBrush);

        _lensRimBrush = _compositor.CreateColorBrush(Color.FromArgb(136, 239, 244, 248));
        AddSolidStroke(_lensOutlinePath, 0.76f, _lensRimBrush);
        AddGradientStroke(_lensOutlinePath, 1.35f, 58, neutral: false);
        AddGradientStroke(_lensInnerPath, 0.62f, 62, neutral: true);
        AddGradientStroke(_lensCausticPath, 0.52f, 68, neutral: false);

        _orbDepthBrush = _compositor.CreateColorBrush(Color.FromArgb(18, 4, 8, 14));
        _orbNeutralBrush = _compositor.CreateColorBrush(Color.FromArgb(11, 246, 249, 252));
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            _orbAccentGradient =
                new RadialGradientBinding(_compositor, centerAlpha: 74, shoulderAlpha: 30);
            _orbAccentBrush = _orbAccentGradient.Brush;
        }
        else
        {
            _orbAccentFallbackBrush =
                _compositor.CreateColorBrush(Color.FromArgb(34, 120, 180, 220));
            _orbAccentBrush = _orbAccentFallbackBrush;
        }
        _orbRefractionBrush = _compositor.CreateColorBrush(Color.FromArgb(42, 120, 180, 220));
        _orbHighlightBrush = _compositor.CreateColorBrush(Color.FromArgb(190, 250, 252, 255));
        _orbSparkBrush = _compositor.CreateColorBrush(Color.FromArgb(126, 250, 252, 255));
        AddFill(_orbDepthGeometry, _orbDepthBrush);
        AddFill(_orbNeutralGeometry, _orbNeutralBrush);
        AddFill(_orbAccentGeometry, _orbAccentBrush);
        AddFill(_orbRefractionGeometry, _orbRefractionBrush);
        AddFill(_orbHighlightGeometry, _orbHighlightBrush);
        AddFill(_orbSparkGeometry, _orbSparkBrush);

        // Nested, slightly eccentric half-ellipses give the embedded lens a
        // refractive mesh character without animating a heavy path collection.
        // Because every ellipse is centered on the monitor boundary, only its
        // intentional inward half is visible.
        for (var index = 0; index < _orbMeshGeometries.Length; index++)
        {
            var normalized = index / (float)(_orbMeshGeometries.Length - 1);
            var neutral = index is 1 or 5;
            AddGradientStroke(
                _orbMeshGeometries[index],
                0.27f + (MathF.Sin(normalized * MathF.PI) * 0.16f),
                (byte)Math.Round(30 + (MathF.Sin(normalized * MathF.PI) * 39)),
                neutral);
        }
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerExited;

    public event EventHandler? Clicked;

    public event EventHandler? SecondaryClicked;

    internal bool OwnsInteractiveWindow(IntPtr windowHandle) =>
        windowHandle == _windowHandle && _pointerInside;

    public static NativeEdgeCompositionHost? TryCreate()
    {
        // The no-redirection composition target can be created and accept frame
        // updates while still presenting no pixels on some Windows/GPU paths.
        // Keep the proven transparent Win2D surface as the production default
        // until that compatibility matrix is validated. Developers can opt in
        // explicitly without putting normal users behind an invisible surface.
        if (!string.Equals(
                Environment.GetEnvironmentVariable(NativeCompositionOptInVariable),
                "1",
                StringComparison.Ordinal))
        {
            LastFailure = "Native composition disabled pending compatibility validation";
            return null;
        }

#if NYKUR_EDGE_VISUAL_TEST
        var scenarioPath = Path.Combine(AppContext.BaseDirectory, "nykur-edge.visual-test");
        try
        {
            if (File.Exists(scenarioPath) &&
                File.ReadAllText(scenarioPath).Contains("fallback", StringComparison.OrdinalIgnoreCase))
            {
                LastFailure = "QA forced Win2D mirror";
                return null;
            }
        }
        catch (IOException)
        {
            // Fall through to the native host; the QA marker is optional.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to the native host; the QA marker is optional.
        }
#endif
        try
        {
            LastFailure = null;
            return new NativeEdgeCompositionHost();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            LastFailure = $"{exception.GetType().Name}: {exception.Message}";
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

    public void Render(EdgeFluidFrame frame)
    {
        ThrowIfDisposed();
        if (_expansionProgress > 0.001 || !_isShown)
        {
            return;
        }

        var baseHeight = frame.OrbScale == EdgeOrbScale.Small ? 31f : 38f;
        var baseReach = frame.OrbScale == EdgeOrbScale.Small ? 16f : 21f;
        var visibleReach = baseReach + ((float)frame.NotificationProgress * 27f);
        var lensHeight = baseHeight + ((float)frame.NotificationProgress * 12f);
        var primaryGeometry = CreateSmoothPath(frame.Primary, frame.OrbAttachmentRadius);
        var secondaryGeometry = CreateSmoothPath(frame.Secondary, frame.OrbAttachmentRadius);
        var interferenceGeometry = CreateSmoothPath(frame.Interference, frame.OrbAttachmentRadius);
        var filamentGeometry = CreateSmoothPath(frame.Filament, frame.OrbAttachmentRadius);
        var lensGeometry = CreateLensGeometry(visibleReach, lensHeight, closeAtEdge: true);
        var lensOutlineGeometry = CreateLensGeometry(visibleReach, lensHeight, closeAtEdge: false);
        var opticalDrift = (float)(
            (Math.Sin(frame.ElapsedSeconds * 0.37) * 0.72) +
            (Math.Sin((frame.ElapsedSeconds * 0.19) + 1.4) * 0.38));
        var lensInnerGeometry = CreateLensGeometry(
            visibleReach * 0.78f,
            lensHeight * 0.72f,
            closeAtEdge: false,
            centerOffset: opticalDrift * 0.35f);
        var lensCausticGeometry = CreateLensGeometry(
            visibleReach * 0.54f,
            lensHeight * 0.43f,
            closeAtEdge: false,
            centerOffset: opticalDrift * 0.62f);

        _primaryPath.Path = new WUC.CompositionPath(primaryGeometry);
        _secondaryPath.Path = new WUC.CompositionPath(secondaryGeometry);
        _interferencePath.Path = new WUC.CompositionPath(interferenceGeometry);
        _filamentPath.Path = new WUC.CompositionPath(filamentGeometry);
        _pendingGeometries.Clear();
        _pendingGeometries.Add(primaryGeometry);
        _pendingGeometries.Add(secondaryGeometry);
        _pendingGeometries.Add(interferenceGeometry);
        _pendingGeometries.Add(filamentGeometry);
        var strandCount = Math.Min(_fineStrandPaths.Length, frame.FineStrands.Length);
        for (var index = 0; index < strandCount; index++)
        {
            var strandGeometry = CreateSmoothPath(
                frame.FineStrands[index],
                frame.OrbAttachmentRadius);
            _fineStrandPaths[index].Path = new WUC.CompositionPath(strandGeometry);
            _pendingGeometries.Add(strandGeometry);
        }
        _lensPath.Path = new WUC.CompositionPath(lensGeometry);
        _lensOutlinePath.Path = new WUC.CompositionPath(lensOutlineGeometry);
        _lensInnerPath.Path = new WUC.CompositionPath(lensInnerGeometry);
        _lensCausticPath.Path = new WUC.CompositionPath(lensCausticGeometry);
        _pendingGeometries.Add(lensGeometry);
        _pendingGeometries.Add(lensOutlineGeometry);
        _pendingGeometries.Add(lensInnerGeometry);
        _pendingGeometries.Add(lensCausticGeometry);

        foreach (var geometry in _currentGeometries)
        {
            geometry.Dispose();
        }
        _currentGeometries.Clear();
        _currentGeometries.AddRange(_pendingGeometries);
        _pendingGeometries.Clear();

        var luminous = frame.PressureField == EdgePressureField.Luminous;
        if (!_hasAccent ||
            !_lastAccent.Equals(frame.Accent) ||
            _lastPressureField != frame.PressureField)
        {
            _lastAccent = frame.Accent;
            _lastPressureField = frame.PressureField;
            _hasAccent = true;
            foreach (var gradient in _accentGradients)
            {
                gradient.Update(frame.Accent);
            }
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            {
                _ambientGlow?.Update(frame.Accent, luminous ? 1f : 0.68f);
                _orbBloom?.Update(frame.Accent, luminous ? 1f : 0.76f);
                _orbAccentGradient?.Update(frame.Accent, 1);
            }
            if (_ambientGlowFallbackBrush is not null)
            {
                _ambientGlowFallbackBrush.Color = Color.FromArgb(
                    (byte)(luminous ? 3 : 2),
                    frame.Accent.R,
                    frame.Accent.G,
                    frame.Accent.B);
            }
            if (_orbBloomFallbackBrush is not null)
            {
                _orbBloomFallbackBrush.Color = Color.FromArgb(
                    (byte)(luminous ? 7 : 5),
                    frame.Accent.R,
                    frame.Accent.G,
                    frame.Accent.B);
            }
            if (_orbAccentFallbackBrush is not null)
            {
                _orbAccentFallbackBrush.Color = Color.FromArgb(
                    38,
                    frame.Accent.R,
                    frame.Accent.G,
                    frame.Accent.B);
            }
            _lensAccentBrush.Color = Color.FromArgb(
                18,
                frame.Accent.R,
                frame.Accent.G,
                frame.Accent.B);
        }

        var energy = Math.Clamp(frame.Energy, 0, 1);
        _orbRefractionBrush.Color = Color.FromArgb(
            (byte)(36 + (energy * 16)),
            frame.Accent.R,
            frame.Accent.G,
            frame.Accent.B);

        var edgeX = _side == EdgeSide.Right ? _widthDip : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var centerY = _heightDip / 2f;
        var radius = lensHeight * 0.37f;

        _ambientGlowGeometry.Center = new Vector2(
            edgeX + (direction * (visibleReach * 0.18f)),
            centerY);
        _ambientGlowGeometry.Radius = new Vector2(
            visibleReach * (2.45f + (energy * 0.24f)),
            lensHeight * (2.5f + (energy * 0.18f)));
        _orbBloomGeometry.Center = new Vector2(edgeX, centerY);
        _orbBloomGeometry.Radius = new Vector2(radius * 1.9f, radius * 2.15f);
        _orbDepthGeometry.Center = new Vector2(edgeX, centerY);
        _orbDepthGeometry.Radius = new Vector2(radius * 1.02f, radius * 1.02f);
        _orbNeutralGeometry.Center = new Vector2(edgeX, centerY);
        _orbNeutralGeometry.Radius = new Vector2(radius * 0.92f, radius * 0.92f);
        _orbAccentGeometry.Center = new Vector2(edgeX, centerY);
        _orbAccentGeometry.Radius = new Vector2(radius * 0.76f, radius * 0.76f);
        _orbRefractionGeometry.Center = new Vector2(
            edgeX + (direction * visibleReach * (0.29f + (opticalDrift * 0.012f))),
            centerY + (opticalDrift * 0.44f));
        _orbRefractionGeometry.Radius = new Vector2(radius * 0.34f, radius * 0.66f);
        _orbHighlightGeometry.Center = new Vector2(
            edgeX + (direction * visibleReach * (0.70f + (opticalDrift * 0.01f))),
            centerY - (lensHeight * (0.235f - (opticalDrift * 0.006f))));
        var highlightRadius = Math.Max(0.72f, visibleReach * 0.055f);
        _orbHighlightGeometry.Radius = new Vector2(highlightRadius, highlightRadius);
        _orbSparkGeometry.Center = new Vector2(
            edgeX + (direction * visibleReach * 0.47f),
            centerY + (lensHeight * (0.21f + (opticalDrift * 0.008f))));
        _orbSparkGeometry.Radius = new Vector2(highlightRadius * 0.55f, highlightRadius * 0.55f);

        for (var index = 0; index < _orbMeshGeometries.Length; index++)
        {
            var normalized = index / (float)(_orbMeshGeometries.Length - 1);
            var phase = (float)Math.Sin(
                (frame.ElapsedSeconds * (0.12 + (index * 0.008))) +
                (index * 0.83));
            var depth = 0.24f + (normalized * 0.70f);
            _orbMeshGeometries[index].Center = new Vector2(
                edgeX + (direction * radius * (0.018f + (phase * 0.018f))),
                centerY + (phase * radius * 0.035f));
            _orbMeshGeometries[index].Radius = new Vector2(
                radius * depth,
                radius * (0.42f + (normalized * 0.54f)));
        }
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
        foreach (var geometry in _pendingGeometries)
        {
            geometry.Dispose();
        }
        _pendingGeometries.Clear();
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

    private CanvasGeometry CreateSmoothPath(Vector2[] points, float orbAttachmentRadius)
    {
        using var builder = new CanvasPathBuilder(_canvasDevice);
        var centerY = _heightDip / 2f;
        var upperEnd = -1;
        var lowerStart = points.Length;
        for (var index = 0; index < points.Length; index++)
        {
            if (points[index].Y <= centerY - orbAttachmentRadius)
            {
                upperEnd = index;
            }

            if (lowerStart == points.Length &&
                points[index].Y >= centerY + orbAttachmentRadius)
            {
                lowerStart = index;
            }
        }

        AppendSmoothFigure(builder, points, 0, upperEnd);
        AppendSmoothFigure(builder, points, lowerStart, points.Length - 1);
        return CanvasGeometry.CreatePath(builder);
    }

    private static void AppendSmoothFigure(
        CanvasPathBuilder builder,
        Vector2[] points,
        int start,
        int end)
    {
        if (start < 0 || end >= points.Length || end - start < 1)
        {
            return;
        }

        builder.BeginFigure(points[start]);
        for (var index = start; index < end; index++)
        {
            var previous = points[Math.Max(start, index - 1)];
            var current = points[index];
            var next = points[index + 1];
            var following = points[Math.Min(end, index + 2)];
            var minimumX = MathF.Min(current.X, next.X);
            var maximumX = MathF.Max(current.X, next.X);
            var controlOne = new Vector2(
                Math.Clamp(current.X + ((next.X - previous.X) / 6f), minimumX, maximumX),
                current.Y + ((next.Y - current.Y) / 3f));
            var controlTwo = new Vector2(
                Math.Clamp(next.X - ((following.X - current.X) / 6f), minimumX, maximumX),
                current.Y + (((next.Y - current.Y) * 2f) / 3f));
            builder.AddCubicBezier(controlOne, controlTwo, next);
        }

        builder.EndFigure(CanvasFigureLoop.Open);
    }

    private CanvasGeometry CreateLensGeometry(
        float visibleReach,
        float lensHeight,
        bool closeAtEdge,
        float centerOffset = 0)
    {
        var edgeX = _side == EdgeSide.Right ? _widthDip : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var centerY = (_heightDip / 2f) + centerOffset;
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

            // WM_NCCREATE is the gate for CreateWindowEx: returning zero aborts
            // creation. Be explicit here instead of forwarding through the
            // partially constructed host instance.
            return new IntPtr(1);
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
        private static readonly float[] Strengths = [0f, 0.12f, 0.58f, 1f, 0.58f, 0.12f, 0f];
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

    [SupportedOSPlatform("windows10.0.18362.0")]
    private sealed class RadialGradientBinding
    {
        private static readonly float[] Offsets = [0f, 0.28f, 0.68f, 1f];
        private static readonly float[] Strengths = [1f, 0.82f, 0.22f, 0f];
        private readonly WUC.CompositionColorGradientStop[] _stops;
        private readonly byte _centerAlpha;
        private readonly byte _shoulderAlpha;

        public RadialGradientBinding(
            WUC.Compositor compositor,
            byte centerAlpha,
            byte shoulderAlpha)
        {
            _centerAlpha = centerAlpha;
            _shoulderAlpha = shoulderAlpha;
            Brush = compositor.CreateRadialGradientBrush();
            Brush.MappingMode = WUC.CompositionMappingMode.Relative;
            Brush.EllipseCenter = new Vector2(0.5f, 0.5f);
            Brush.EllipseRadius = new Vector2(0.5f, 0.5f);
            Brush.GradientOriginOffset = new Vector2(-0.08f, -0.05f);
            _stops = new WUC.CompositionColorGradientStop[Offsets.Length];
            for (var index = 0; index < Offsets.Length; index++)
            {
                _stops[index] = compositor.CreateColorGradientStop();
                _stops[index].Offset = Offsets[index];
                Brush.ColorStops.Append(_stops[index]);
            }
            Update(Color.FromArgb(255, 120, 180, 220), 1);
        }

        public WUC.CompositionRadialGradientBrush Brush { get; }

        public void Update(Color accent, float intensity)
        {
            intensity = Math.Clamp(intensity, 0, 1.5f);
            for (var index = 0; index < _stops.Length; index++)
            {
                var baseAlpha = index <= 1 ? _centerAlpha : _shoulderAlpha;
                var alpha = (byte)Math.Clamp(
                    (int)Math.Round(baseAlpha * Strengths[index] * intensity),
                    0,
                    255);
                _stops[index].Color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
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
