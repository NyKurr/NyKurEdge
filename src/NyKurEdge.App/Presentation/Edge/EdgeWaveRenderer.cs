using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.App.Presentation.Animations;
using NyKurEdge.Core.Settings;
using Windows.UI;

namespace NyKurEdge.App.Presentation.Edge;

public enum EdgeFluidCharacter { Calm, Balanced, Expressive }

public enum EdgeOrbScale { Small, Medium }

public enum EdgeShellShape { SoftCapsule, TaperedBloom }

public enum EdgePressureField { Airy, Luminous }

public enum EdgeVerticalReach { Focused, Extended }

internal readonly record struct EdgeFluidFrame(
    Vector2[] Primary,
    Vector2[] Secondary,
    Vector2[] Interference,
    Vector2[] Filament,
    Vector2[][] FineStrands,
    Color Accent,
    EdgeOrbScale OrbScale,
    EdgePressureField PressureField,
    double NotificationProgress,
    float Energy,
    double ElapsedSeconds);

/// <summary>
/// Renders the ambient edge as one continuously presented fluid field. Slow,
/// coherent simulation targets feed spring-interpolated contours; rendering is
/// synchronized to the desktop compositor so neither the wave nor the lens
/// advances in visible snapshots.
/// </summary>
public sealed class EdgeWaveRenderer : IDisposable
{
    private const int ControlPointCount = 15;
    private const int RenderPointCount = 73;
    internal const int FineStrandCount = 17;
    private const double MaximumPresentationRate = 60;

    private readonly CanvasControl _canvas;
    private readonly EdgeWindowController _windowController;
    private readonly SolidColorBrush _accentBrush;
    private readonly IEdgeMotionSource _motionSource;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CanvasStrokeStyle _roundedStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };
    private readonly FluidNode[] _primaryNodes = new FluidNode[ControlPointCount];
    private readonly FluidNode[] _secondaryNodes = new FluidNode[ControlPointCount];
    private readonly FluidNode[] _tertiaryNodes = new FluidNode[ControlPointCount];
    private readonly Vector2[] _primaryPoints = new Vector2[RenderPointCount];
    private readonly Vector2[] _secondaryPoints = new Vector2[RenderPointCount];
    private readonly Vector2[] _interferencePoints = new Vector2[RenderPointCount];
    private readonly Vector2[] _filamentPoints = new Vector2[RenderPointCount];
    private readonly Vector2[][] _fineStrandPoints = CreatePointMatrix(FineStrandCount, RenderPointCount);
    private CanvasGeometry? _cachedLens;
    private CanvasGeometry? _cachedLensOutline;
    private LensCacheKey _lensCacheKey;
    private double _lastFrameSeconds;
    private double _lastRenderRequestSeconds = double.NegativeInfinity;
    private double _nextTargetSeconds;
    private double _notificationStartedAt = double.NegativeInfinity;
    private double _notificationTimingScale = 1;
    private double _lastNotificationIconProgress = -1;
    private double _lastNotificationExpansionProgress = -1;
    private double _expansionProgress;
    private double _intensity = 1;
    private bool _isPlaying;
    private bool _isRunning;
    private bool _hasSimulationState;
    private bool _disposed;
    private EdgeSide _side = EdgeSide.Right;
    private EdgeFluidCharacter _character = EdgeFluidCharacter.Balanced;
    private EdgeOrbScale _orbScale = EdgeOrbScale.Medium;
    private EdgeShellShape _shellShape = EdgeShellShape.SoftCapsule;
    private EdgePressureField _pressureField = EdgePressureField.Luminous;
    private EdgeVerticalReach _verticalReach = EdgeVerticalReach.Extended;

    public EdgeWaveRenderer(
        CanvasControl canvas,
        SolidColorBrush accentBrush,
        EdgeWindowController windowController,
        IEdgeMotionSource? motionSource = null)
    {
        _canvas = canvas;
        _accentBrush = accentBrush;
        _windowController = windowController;
        _motionSource = motionSource ?? new ProceduralEdgeMotionSource();
        _canvas.UseSharedDevice = true;
        _canvas.ClearColor = Color.FromArgb(0, 0, 0, 0);
        _canvas.CreateResources += OnCreateResources;
        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += OnSizeChanged;
    }

    public EdgeFluidCharacter Character => _character;

    public EdgeOrbScale OrbScale => _orbScale;

    public EdgeShellShape ShellShape => _shellShape;

    public EdgePressureField PressureField => _pressureField;

    public EdgeVerticalReach VerticalReach => _verticalReach;

    public event Action<double>? NotificationIconProgressChanged;

    public event Action<double>? NotificationExpansionProgressChanged;

    public void Start()
    {
        ThrowIfDisposed();
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _lastFrameSeconds = _clock.Elapsed.TotalSeconds;
        _lastRenderRequestSeconds = double.NegativeInfinity;
        CompositionTarget.Rendering += OnRendering;
        _canvas.Invalidate();
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    public void SetPlaying(bool isPlaying)
    {
        if (_isPlaying == isPlaying)
        {
            return;
        }

        _isPlaying = isPlaying;
        _nextTargetSeconds = 0;
        Invalidate();
    }

    public void SetIntensity(AnimationIntensity intensity)
    {
        _intensity = intensity switch
        {
            AnimationIntensity.Quiet => 0.72,
            AnimationIntensity.Expressive => 1.22,
            _ => 1,
        };
        _nextTargetSeconds = 0;
        Invalidate();
    }

    public void SetSide(EdgeSide side)
    {
        if (_side == side)
        {
            return;
        }

        _side = side;
        InvalidateLensCache();
        Invalidate();
    }

    public void SetExpansionProgress(double progress)
    {
        _expansionProgress = Math.Clamp(progress, 0, 1);
        Invalidate();
    }

    public void TriggerNotificationPulse(double timingScale = 1)
    {
        _notificationTimingScale = Math.Clamp(timingScale, 1, 4);
        _notificationStartedAt = _clock.Elapsed.TotalSeconds;
        InvalidateLensCache();
        Invalidate();
    }

    public EdgeFluidCharacter CycleCharacter()
    {
        _character = _character switch
        {
            EdgeFluidCharacter.Calm => EdgeFluidCharacter.Balanced,
            EdgeFluidCharacter.Balanced => EdgeFluidCharacter.Expressive,
            _ => EdgeFluidCharacter.Calm,
        };
        _nextTargetSeconds = 0;
        Invalidate();
        return _character;
    }

    public EdgePressureField CyclePressureField()
    {
        _pressureField = _pressureField == EdgePressureField.Airy
            ? EdgePressureField.Luminous
            : EdgePressureField.Airy;
        Invalidate();
        return _pressureField;
    }

    public EdgeVerticalReach CycleVerticalReach()
    {
        _verticalReach = _verticalReach == EdgeVerticalReach.Focused
            ? EdgeVerticalReach.Extended
            : EdgeVerticalReach.Focused;
        _nextTargetSeconds = 0;
        Invalidate();
        return _verticalReach;
    }

    public EdgeOrbScale CycleOrbScale()
    {
        _orbScale = _orbScale == EdgeOrbScale.Small ? EdgeOrbScale.Medium : EdgeOrbScale.Small;
        InvalidateLensCache();
        Invalidate();
        return _orbScale;
    }

    public EdgeShellShape CycleShellShape()
    {
        _shellShape = _shellShape == EdgeShellShape.SoftCapsule
            ? EdgeShellShape.TaperedBloom
            : EdgeShellShape.SoftCapsule;
        InvalidateLensCache();
        Invalidate();
        return _shellShape;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _canvas.CreateResources -= OnCreateResources;
        _canvas.Draw -= OnDraw;
        _canvas.SizeChanged -= OnSizeChanged;
        _canvas.RemoveFromVisualTree();
        _roundedStroke.Dispose();
        InvalidateLensCache();
        _clock.Stop();
    }

    private void OnRendering(object? sender, object args)
    {
        if (!_isRunning || _disposed)
        {
            return;
        }

        var seconds = _clock.Elapsed.TotalSeconds;
        if (seconds - _lastRenderRequestSeconds < 1d / MaximumPresentationRate)
        {
            return;
        }

        _lastRenderRequestSeconds = seconds;
        _canvas.Invalidate();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateLensCache();
        Invalidate();
    }

    private void OnCreateResources(
        CanvasControl sender,
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args) =>
        InvalidateLensCache();

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var width = (float)sender.ActualWidth;
        var height = (float)sender.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var seconds = _clock.Elapsed.TotalSeconds;
        var deltaSeconds = Math.Clamp(seconds - _lastFrameSeconds, 0, 0.12);
        _lastFrameSeconds = seconds;
        var signal = _motionSource.Sample(seconds, _isPlaying).Normalize();

        if (seconds >= _nextTargetSeconds)
        {
            UpdateTargets(seconds, signal);
            _nextTargetSeconds = seconds + (_isPlaying ? 0.045 : 0.085);
            if (!_hasSimulationState)
            {
                SnapNodesToTargets();
                _hasSimulationState = true;
            }
        }

        while (deltaSeconds > 0)
        {
            var step = Math.Min(deltaSeconds, 1d / 120d);
            IntegrateNodes(step);
            deltaSeconds -= step;
        }

        BuildContours(width, height, seconds, signal);
        if (!_windowController.HasNativeCollapsedSurface || _expansionProgress > 0.001)
        {
            DrawFluid(args.DrawingSession, sender, width, height, seconds, signal);
        }
        var notificationAge = (seconds - _notificationStartedAt) / _notificationTimingScale;
        _windowController.RenderCollapsedEdge(new EdgeFluidFrame(
            _primaryPoints,
            _secondaryPoints,
            _interferencePoints,
            _filamentPoints,
            _fineStrandPoints,
            _accentBrush.Color,
            _orbScale,
            _pressureField,
            NotificationLensProgress(notificationAge),
            (float)signal.Energy,
            seconds));
    }

    private void UpdateTargets(double seconds, EdgeMotionSignal signal)
    {
        var characterScale = _character switch
        {
            EdgeFluidCharacter.Calm => 0.62,
            EdgeFluidCharacter.Expressive => 1.26,
            _ => 1,
        };
        var activity = (0.34 + (signal.Energy * 1.42)) * _intensity * characterScale;

        for (var index = 0; index < ControlPointCount; index++)
        {
            var normalized = index / (double)(ControlPointCount - 1);
            var presence = VerticalPresence(normalized);
            var slowPrimary = SignedNoise((seconds * 0.115) + (index * 0.19), 17 + (index * 23));
            var finePrimary = SignedNoise((seconds * 0.31) + (index * 0.11), 59 + (index * 31));
            var slowSecondary = SignedNoise((seconds * 0.093) + (index * 0.23), 101 + (index * 19));
            var fineSecondary = SignedNoise((seconds * 0.27) + (index * 0.17), 149 + (index * 29));
            var slowTertiary = SignedNoise((seconds * 0.071) + (index * 0.27), 227 + (index * 17));
            var fineTertiary = SignedNoise((seconds * 0.22) + (index * 0.13), 307 + (index * 37));

            _primaryNodes[index].Target =
                ((slowPrimary * 0.74) + (finePrimary * 0.26)) * activity * presence;
            _secondaryNodes[index].Target =
                ((slowSecondary * 0.78) + (fineSecondary * 0.22)) * activity * presence * 0.82;
            _tertiaryNodes[index].Target =
                ((slowTertiary * 0.83) + (fineTertiary * 0.17)) * activity * presence * 0.68;
        }
    }

    private void SnapNodesToTargets()
    {
        for (var index = 0; index < ControlPointCount; index++)
        {
            _primaryNodes[index].Value = _primaryNodes[index].Target;
            _primaryNodes[index].Velocity = 0;
            _secondaryNodes[index].Value = _secondaryNodes[index].Target;
            _secondaryNodes[index].Velocity = 0;
            _tertiaryNodes[index].Value = _tertiaryNodes[index].Target;
            _tertiaryNodes[index].Velocity = 0;
        }
    }

    private void IntegrateNodes(double deltaSeconds)
    {
        var stiffness = _isPlaying ? 18.5 : 10.5;
        var damping = _isPlaying ? 8.4 : 6.4;
        for (var index = 0; index < ControlPointCount; index++)
        {
            Integrate(ref _primaryNodes[index], deltaSeconds, stiffness, damping);
            Integrate(ref _secondaryNodes[index], deltaSeconds, stiffness * 0.82, damping * 1.04);
            Integrate(ref _tertiaryNodes[index], deltaSeconds, stiffness * 0.68, damping * 1.12);
        }
    }

    private void BuildContours(float width, float height, double seconds, EdgeMotionSignal signal)
    {
        var edgeX = _side == EdgeSide.Right ? width : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var playingReach = _isPlaying ? 4.5f + ((float)signal.LowBand * 6.5f) : 0f;
        var expression = _character == EdgeFluidCharacter.Expressive ? 4.2f : 0f;
        var expansionReach = (float)(SmoothStep(_expansionProgress) * 58);
        var notificationAge = (seconds - _notificationStartedAt) / _notificationTimingScale;

        for (var index = 0; index < RenderPointCount; index++)
        {
            var normalized = index / (double)(RenderPointCount - 1);
            var presence = VerticalPresence(normalized);
            var center = Gaussian(normalized, 0.5, 2.14);
            var orbChannel = Gaussian(normalized, 0.5, 15.8);
            var shoulders =
                Gaussian(normalized, 0.435, 20.5) +
                Gaussian(normalized, 0.565, 20.5);
            var distantFlow =
                Gaussian(normalized, 0.285, 9.6) +
                Gaussian(normalized, 0.715, 9.6);
            var splitAroundOrb = 1 - (orbChannel * 0.36);
            var baseReach = presence *
                            (2.8 +
                             ((27.5 + (playingReach * 1.18) + expression) * center) +
                             (7.2 * shoulders) +
                             (4.1 * distantFlow)) *
                            splitAroundOrb;
            var primaryDrift = SampleNode(_primaryNodes, normalized) *
                               (3.2 + (signal.MidBand * 4.5));
            var secondaryDrift = SampleNode(_secondaryNodes, normalized) *
                                 (2.7 + (signal.HighBand * 3.7));
            var tertiaryDrift = SampleNode(_tertiaryNodes, normalized) *
                                (2.1 + (signal.MidBand * 2.8));
            var fieldDrift = SignedNoise((seconds * 0.072) + (normalized * 2.7), 211) *
                             presence * 2.15;
            var notification = NotificationDisplacement(normalized, notificationAge);
            var expansion = expansionReach * Gaussian(normalized, 0.5, 2.18);
            var y = (float)(normalized * height);

            // Keep the structural contours at meaningfully different depths.
            // Earlier revisions placed every contour around the same reach and
            // the result collapsed into one heavy cable on a real desktop.
            var primaryReach = Math.Clamp(
                (baseReach * 2.02) + (primaryDrift * 0.86) + fieldDrift +
                notification + expansion,
                0.15,
                width - 2);
            var secondaryReach = Math.Clamp(
                (baseReach * 1.26) + secondaryDrift - (fieldDrift * 0.26) +
                (notification * 0.62) + (expansion * 0.93),
                0.1,
                width - 2);
            var interferenceReach = Math.Clamp(
                (baseReach * 2.48) - (primaryDrift * 0.24) + (secondaryDrift * 0.34) +
                (notification * 0.78) + (expansion * 0.98),
                0.1,
                width - 2);
            var filamentReach = Math.Clamp(
                (baseReach * 0.42) + (primaryDrift * 0.14) + (secondaryDrift * 0.10) +
                (notification * 0.31) + (expansion * 0.86),
                0.08,
                width - 2);

            _primaryPoints[index] = new Vector2(edgeX + (direction * (float)primaryReach), y);
            _secondaryPoints[index] = new Vector2(edgeX + (direction * (float)secondaryReach), y);
            _interferencePoints[index] = new Vector2(edgeX + (direction * (float)interferenceReach), y);
            _filamentPoints[index] = new Vector2(edgeX + (direction * (float)filamentReach), y);

            for (var strandIndex = 0; strandIndex < FineStrandCount; strandIndex++)
            {
                // Depth runs from the edge-side filament toward the outer fluid
                // boundary. Keeping every lane positive prevents half the family
                // from being clamped into an indistinguishable seam at the HWND
                // boundary, while signedLane still provides top/bottom refraction.
                var laneDepth = strandIndex / (double)(FineStrandCount - 1);
                var signedLane = (laneDepth * 2) - 1;
                var laneCurve = Math.Pow(laneDepth, 0.86);
                var laneBlend = SmootherStep(laneDepth);
                var drift = Lerp(primaryDrift, secondaryDrift, laneBlend) +
                            (tertiaryDrift * (1 - Math.Abs(signedLane)) * 0.62);
                var ribbonWidth = presence *
                                  (9.6 + (50.5 * center) + (6.8 * shoulders));
                var separationFlow = SignedNoise(
                    (seconds * (0.036 + (strandIndex * 0.0011))) +
                    (normalized * 1.72) +
                    (strandIndex * 0.43),
                    317 + (strandIndex * 47));
                var laneBreath = 0.86 +
                                 (separationFlow * (0.08 + (laneDepth * 0.13)));
                var laneOffset = laneCurve * ribbonWidth * laneBreath;
                var fieldFold = SignedNoise(
                    (seconds * (0.052 + (strandIndex * 0.0017))) +
                    (normalized * 3.25) +
                    (strandIndex * 0.37),
                    401 + (strandIndex * 43)) * presence * (1.25 + (center * 3.35));
                var interferenceFold = SignedNoise(
                    (seconds * 0.084) -
                    (normalized * 2.15) +
                    (strandIndex * 0.21),
                    733 + (strandIndex * 29)) * presence * (0.92 + (laneDepth * 0.58));
                var orbPressure = laneCurve * orbChannel * (5.8 + (signal.Energy * 2.8));
                var strandReach = Math.Clamp(
                    (baseReach * (0.18 + (laneDepth * 0.46))) +
                    (drift * 0.72) +
                    laneOffset +
                    fieldFold +
                    interferenceFold +
                    orbPressure +
                    (notification * (0.32 + (0.38 * (1 - Math.Abs(signedLane))))) +
                    (expansion * (0.84 + (0.12 * laneBlend))),
                    0.08,
                    width - 2);
                var verticalWrap = signedLane * orbChannel * (10.2 + (signal.Energy * 2.6));
                var microLift = SignedNoise(
                    (seconds * 0.061) +
                    (normalized * 2.7) +
                    (strandIndex * 0.31),
                    977 + (strandIndex * 23)) * presence * 0.62;
                var strandY = Math.Clamp(y + (float)(verticalWrap + microLift), 0, height);
                _fineStrandPoints[strandIndex][index] = new Vector2(
                    edgeX + (direction * (float)strandReach),
                    strandY);
            }
        }
    }

    private void DrawFluid(
        CanvasDrawingSession drawingSession,
        CanvasControl canvas,
        float width,
        float height,
        double seconds,
        EdgeMotionSignal signal)
    {
        var accent = _accentBrush.Color;
        var energy = (float)signal.Energy;
        var fieldScale = _pressureField == EdgePressureField.Airy ? 0.78f : 1.16f;

        // Broad strokes replace the old closed fill. Their alpha is deliberately
        // tiny: together they imply refracted volume without creating a slab.
        DrawContour(drawingSession, _primaryPoints, accent, 18f, (byte)(2 * fieldScale), height);
        DrawContour(drawingSession, _primaryPoints, accent, 9f, (byte)((4 + energy) * fieldScale), height);
        DrawContour(drawingSession, _primaryPoints, accent, 3.4f, (byte)((7 + (energy * 2)) * fieldScale), height);
        DrawContour(drawingSession, _secondaryPoints, accent, 6f, (byte)((3 + energy) * fieldScale), height);

        DrawContour(drawingSession, _interferencePoints, accent, 0.58f, 18, height);
        DrawContour(drawingSession, _secondaryPoints, accent, 0.72f, 31, height);
        DrawContour(drawingSession, _primaryPoints, accent, 0.82f, (byte)(43 + (energy * 14)), height);

        for (var strandIndex = 0; strandIndex < FineStrandCount; strandIndex++)
        {
            var normalizedLane = strandIndex / (float)(FineStrandCount - 1);
            var centerEmphasis = MathF.Sin(normalizedLane * MathF.PI);
            var isOpticalHighlight = strandIndex is 4 or 12;
            var strandColor = isOpticalHighlight
                ? Color.FromArgb(255, 244, 248, 252)
                : accent;
            var alpha = (byte)Math.Clamp(
                (int)Math.Round((82 + (centerEmphasis * 94) + (energy * 18)) * fieldScale),
                12,
                210);
            var widthScale = 0.46f + (centerEmphasis * 0.42f);
            DrawContour(
                drawingSession,
                _fineStrandPoints[strandIndex],
                strandColor,
                widthScale,
                alpha,
                height);
        }
        DrawContour(
            drawingSession,
            _primaryPoints,
            Color.FromArgb(255, 246, 249, 252),
            0.32f,
            18,
            height);
        DrawContour(
            drawingSession,
            _filamentPoints,
            Color.FromArgb(255, 246, 250, 253),
            0.48f,
            27,
            height);

        DrawMorphingLens(
            drawingSession,
            canvas,
            width,
            height,
            seconds,
            SmoothStep(_expansionProgress),
            accent,
            signal);
    }

    private void DrawMorphingLens(
        CanvasDrawingSession drawingSession,
        CanvasControl canvas,
        float width,
        float height,
        double seconds,
        double expansion,
        Color accent,
        EdgeMotionSignal signal)
    {
        var notificationAge = (seconds - _notificationStartedAt) / _notificationTimingScale;
        var notification = NotificationLensProgress(notificationAge);
        var iconProgress = SmoothStep(Math.Clamp((notification - 0.16) / 0.76, 0, 1));
        PublishNotificationProgress(notification, iconProgress);

        var baseHeight = _orbScale == EdgeOrbScale.Small ? 31f : 38f;
        var baseReach = _orbScale == EdgeOrbScale.Small ? 16f : 21f;
        var targetHeight = _shellShape == EdgeShellShape.SoftCapsule ? 272f : 258f;
        var horizontalProgress = (float)(1 - Math.Pow(1 - expansion, 3.4));
        var verticalProgress = (float)Math.Pow(expansion, 0.66);
        var visibleReach = Lerp(
            baseReach + ((float)notification * 27f),
            Math.Max(baseReach, width - 10f),
            horizontalProgress);
        var lensHeight = Lerp(baseHeight + ((float)notification * 12f), targetHeight, verticalProgress);
        var centerY = height / 2f;

        var isStableShape = notification <= 0.0001 &&
                            (expansion <= 0.0001 || expansion >= 0.9999);
        CanvasGeometry? transientShell = null;
        CanvasGeometry? transientOutline = null;
        CanvasGeometry shell;
        CanvasGeometry outline;
        if (isStableShape)
        {
            var key = new LensCacheKey(
                width,
                height,
                visibleReach,
                lensHeight,
                expansion >= 0.9999,
                _side,
                _shellShape,
                _orbScale);
            if (_cachedLens is null || _cachedLensOutline is null || !_lensCacheKey.Equals(key))
            {
                InvalidateLensCache();
                _cachedLens = CreateLensGeometry(
                    canvas,
                    width,
                    centerY,
                    visibleReach,
                    lensHeight,
                    expansion);
                _cachedLensOutline = CreateLensGeometry(
                    canvas,
                    width,
                    centerY,
                    visibleReach,
                    lensHeight,
                    expansion,
                    closeAtEdge: false);
                _lensCacheKey = key;
            }

            shell = _cachedLens;
            outline = _cachedLensOutline;
        }
        else
        {
            transientShell = CreateLensGeometry(
                canvas,
                width,
                centerY,
                visibleReach,
                lensHeight,
                expansion);
            transientOutline = CreateLensGeometry(
                canvas,
                width,
                centerY,
                visibleReach,
                lensHeight,
                expansion,
                closeAtEdge: false);
            shell = transientShell;
            outline = transientOutline;
        }

        try
        {
            drawingSession.DrawGeometry(outline, Color.FromArgb(14, accent.R, accent.G, accent.B), 12f);
            drawingSession.DrawGeometry(outline, Color.FromArgb(25, accent.R, accent.G, accent.B), 4.2f);
            drawingSession.FillGeometry(shell, Color.FromArgb((byte)Lerp(43, 142, (float)expansion), 8, 12, 18));
            drawingSession.FillGeometry(
                shell,
                Color.FromArgb((byte)Lerp(15, 7, (float)expansion), accent.R, accent.G, accent.B));
            drawingSession.DrawGeometry(
                outline,
                Color.FromArgb((byte)Lerp(92, 53, (float)expansion), 239, 244, 248),
                0.76f);
            drawingSession.DrawGeometry(
                outline,
                Color.FromArgb((byte)Lerp(57, 30, (float)expansion), accent.R, accent.G, accent.B),
                1.35f);

            if (expansion < 0.58)
            {
                DrawLensOptics(
                    drawingSession,
                    width,
                    centerY,
                    visibleReach,
                    lensHeight,
                    accent,
                    (float)(1 - Math.Clamp(expansion / 0.58, 0, 1)),
                    (float)signal.Energy);
            }
        }
        finally
        {
            transientOutline?.Dispose();
            transientShell?.Dispose();
        }
    }

    private void PublishNotificationProgress(double expansionProgress, double iconProgress)
    {
        if (Math.Abs(expansionProgress - _lastNotificationExpansionProgress) > 0.012 ||
            expansionProgress is 0 or 1)
        {
            _lastNotificationExpansionProgress = expansionProgress;
            NotificationExpansionProgressChanged?.Invoke(expansionProgress);
        }

        if (Math.Abs(iconProgress - _lastNotificationIconProgress) > 0.012 || iconProgress is 0 or 1)
        {
            _lastNotificationIconProgress = iconProgress;
            NotificationIconProgressChanged?.Invoke(iconProgress);
        }
    }

    private CanvasGeometry CreateLensGeometry(
        CanvasControl canvas,
        float width,
        float centerY,
        float visibleReach,
        float lensHeight,
        double expansion,
        bool closeAtEdge = true)
    {
        var edgeX = _side == EdgeSide.Right ? width : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var innerX = edgeX + (direction * visibleReach);
        var top = centerY - (lensHeight / 2f);
        var bottom = centerY + (lensHeight / 2f);
        var halfHeight = lensHeight / 2f;
        var corner = Lerp(halfHeight, Math.Min(34f, halfHeight * 0.46f), (float)expansion);
        var innerTop = top + corner;
        var innerBottom = bottom - corner;
        var taper = _shellShape == EdgeShellShape.TaperedBloom ? (float)(12 * expansion) : 0f;
        const float ellipseKappa = 0.5522848f;

        using var builder = new CanvasPathBuilder(canvas);
        builder.BeginFigure(new Vector2(edgeX, top));
        builder.AddCubicBezier(
            new Vector2(edgeX + (direction * visibleReach * ellipseKappa), top - taper),
            new Vector2(innerX, innerTop - (corner * ellipseKappa)),
            new Vector2(innerX, innerTop));
        if (innerBottom > innerTop)
        {
            builder.AddCubicBezier(
                new Vector2(innerX - (direction * 2.5f * (float)expansion), centerY - (corner * 0.10f)),
                new Vector2(innerX - (direction * 2.5f * (float)expansion), centerY + (corner * 0.10f)),
                new Vector2(innerX, innerBottom));
        }
        builder.AddCubicBezier(
            new Vector2(innerX, innerBottom + (corner * ellipseKappa)),
            new Vector2(edgeX + (direction * visibleReach * ellipseKappa), bottom + taper),
            new Vector2(edgeX, bottom));
        builder.EndFigure(closeAtEdge ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }

    private void DrawLensOptics(
        CanvasDrawingSession drawingSession,
        float width,
        float centerY,
        float visibleReach,
        float lensHeight,
        Color accent,
        float opacity,
        float energy)
    {
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var edgeX = _side == EdgeSide.Right ? width : 0f;
        var opticalEnergy = Math.Clamp(energy, 0f, 1f);
        var radius = lensHeight * 0.37f;

        drawingSession.FillCircle(
            new Vector2(edgeX, centerY),
            radius * 2.4f,
            Color.FromArgb((byte)(3 * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            new Vector2(edgeX, centerY),
            radius * 1.72f,
            Color.FromArgb((byte)(6 * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            new Vector2(edgeX, centerY),
            radius,
            Color.FromArgb((byte)((9 + (opticalEnergy * 4)) * opacity), 246, 249, 252));
        drawingSession.FillCircle(
            new Vector2(edgeX, centerY),
            radius * 0.82f,
            Color.FromArgb(
                (byte)((18 + (opticalEnergy * 11)) * opacity),
                accent.R,
                accent.G,
                accent.B));
        drawingSession.DrawCircle(
            new Vector2(edgeX, centerY),
            radius * 0.88f,
            Color.FromArgb((byte)(34 * opacity), 247, 250, 253),
            0.56f);
        drawingSession.DrawCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.08f), centerY),
            radius * 0.64f,
            Color.FromArgb((byte)(24 * opacity), accent.R, accent.G, accent.B),
            0.42f);
        drawingSession.DrawCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.18f), centerY - (lensHeight * 0.025f)),
            radius * 0.43f,
            Color.FromArgb((byte)(18 * opacity), 247, 250, 253),
            0.34f);

        drawingSession.FillCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.62f), centerY - (lensHeight * 0.11f)),
            Math.Max(2.4f, visibleReach * 0.22f),
            Color.FromArgb((byte)((28 + (opticalEnergy * 9)) * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.73f), centerY - (lensHeight * 0.24f)),
            Math.Max(0.8f, visibleReach * 0.062f),
            Color.FromArgb((byte)(190 * opacity), 250, 252, 255));

        var top = centerY - (lensHeight * 0.39f);
        drawingSession.DrawLine(
            new Vector2(edgeX + (direction * visibleReach * 0.24f), top),
            new Vector2(
                edgeX + (direction * visibleReach * 0.78f),
                centerY - (lensHeight * 0.19f)),
            Color.FromArgb((byte)(124 * opacity), 250, 252, 255),
            0.82f,
            _roundedStroke);
    }

    private void InvalidateLensCache()
    {
        _cachedLensOutline?.Dispose();
        _cachedLensOutline = null;
        _cachedLens?.Dispose();
        _cachedLens = null;
        _lensCacheKey = default;
    }

    private void DrawContour(
        CanvasDrawingSession drawingSession,
        Vector2[] points,
        Color color,
        float strokeWidth,
        byte peakAlpha,
        float height)
    {
        for (var index = 1; index < points.Length; index++)
        {
            var midpointY = (points[index - 1].Y + points[index].Y) / 2f;
            var normalized = midpointY / Math.Max(1f, height);
            var opacity = VerticalOpacity(normalized);
            var alpha = (byte)Math.Clamp((int)Math.Round(peakAlpha * opacity), 0, 255);
            if (alpha < 2)
            {
                continue;
            }

            drawingSession.DrawLine(
                points[index - 1],
                points[index],
                Color.FromArgb(alpha, color.R, color.G, color.B),
                strokeWidth,
                _roundedStroke);
        }
    }

    private double VerticalPresence(double normalized)
    {
        var fadeDistance = _verticalReach == EdgeVerticalReach.Extended ? 0.105 : 0.18;
        var edgeFade = SmootherStep(Math.Clamp(normalized / fadeDistance, 0, 1)) *
                       SmootherStep(Math.Clamp((1 - normalized) / fadeDistance, 0, 1));
        var centerSharpness = _verticalReach == EdgeVerticalReach.Extended ? 2.05 : 2.55;
        var broadCenter = Gaussian(normalized, 0.5, centerSharpness);
        var floor = _verticalReach == EdgeVerticalReach.Extended ? 0.17 : 0.10;
        return edgeFade * (floor + ((1 - floor) * broadCenter));
    }

    private double VerticalOpacity(double normalized)
    {
        var presence = VerticalPresence(normalized);
        return Math.Pow(Math.Clamp(presence, 0, 1), 0.72);
    }

    private void Invalidate()
    {
        if (!_disposed)
        {
            _canvas.Invalidate();
        }
    }

    private static void Integrate(ref FluidNode node, double deltaSeconds, double stiffness, double damping)
    {
        var acceleration = ((node.Target - node.Value) * stiffness) - (node.Velocity * damping);
        node.Velocity += acceleration * deltaSeconds;
        node.Value += node.Velocity * deltaSeconds;
    }

    private static double SampleNode(FluidNode[] nodes, double normalized)
    {
        var scaled = normalized * (nodes.Length - 1);
        var index = Math.Clamp((int)Math.Floor(scaled), 0, nodes.Length - 2);
        return Lerp(nodes[index].Value, nodes[index + 1].Value, SmootherStep(scaled - index));
    }

    private static double NotificationDisplacement(double normalizedY, double age)
    {
        if (age is < 0 or > 1.92)
        {
            return 0;
        }

        var incomingProgress = Math.Clamp(age / 0.42, 0, 1);
        var incomingCenter = 0.13 + (SmootherStep(incomingProgress) * 0.37);
        var incomingEnvelope = 1 - SmoothStep(Math.Clamp((age - 0.28) / 0.22, 0, 1));
        var incoming = Gaussian(normalizedY, incomingCenter, 29) * incomingEnvelope * 13;

        if (age < 0.26)
        {
            return incoming;
        }

        var rippleProgress = Math.Clamp((age - 0.26) / 1.62, 0, 1);
        var radius = SmootherStep(rippleProgress) * 0.49;
        var ripple = Math.Exp(-Math.Pow((Math.Abs(normalizedY - 0.5) - radius) * 24, 2)) *
                     Math.Pow(1 - rippleProgress, 1.2) * 17;
        var wake = -Gaussian(normalizedY, 0.5, 13) *
                   Math.Sin(Math.PI * Math.Clamp((age - 0.35) / 0.9, 0, 1)) * 2.4;
        return incoming + ripple + wake;
    }

    private static double NotificationLensProgress(double age)
    {
        if (age is < 0 or > 1.84)
        {
            return 0;
        }

        if (age < 0.28)
        {
            return SmootherStep(age / 0.28);
        }

        return age < 1.16 ? 1 : 1 - SmootherStep((age - 1.16) / 0.68);
    }

    private static double SignedNoise(double value, int seed)
    {
        var left = (int)Math.Floor(value);
        var amount = SmootherStep(value - left);
        return Lerp(SignedHash(left, seed), SignedHash(left + 1, seed), amount);
    }

    private static double SignedHash(int value, int seed)
    {
        unchecked
        {
            var hash = (uint)(value * 374761393 + seed * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177;
            hash ^= hash >> 16;
            return ((hash & 0x00FFFFFF) / (double)0x007FFFFF) - 1;
        }
    }

    private static double Gaussian(double value, double center, double sharpness) =>
        Math.Exp(-Math.Pow((value - center) * sharpness, 2));

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - (2 * value));
    }

    private static double SmootherStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * value * ((value * ((value * 6) - 15)) + 10);
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);

    private static Vector2[][] CreatePointMatrix(int rows, int columns)
    {
        var result = new Vector2[rows][];
        for (var row = 0; row < rows; row++)
        {
            result[row] = new Vector2[columns];
        }
        return result;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private struct FluidNode
    {
        public double Value;
        public double Velocity;
        public double Target;
    }

    private readonly record struct LensCacheKey(
        float Width,
        float Height,
        float VisibleReach,
        float LensHeight,
        bool Expanded,
        EdgeSide Side,
        EdgeShellShape ShellShape,
        EdgeOrbScale OrbScale);
}
