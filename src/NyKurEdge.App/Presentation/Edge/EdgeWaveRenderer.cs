using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
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
    float OrbAttachmentRadius,
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
    private const int ControlPointCount = 17;
    // Catmull-Rom-to-Bezier interpolation keeps these sparse samples smooth;
    // the fallback path no longer needs thousands of tiny line segments.
    private const int RenderPointCount = 73;
    internal const int FineStrandCount = 32;
    private const int FineStrandGeometryGroupCount = 6;
    private const double IdlePresentationRate = 36;
    private const double ActivePresentationRate = 60;

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
    private readonly CanvasGeometry?[] _fineStrandGeometries =
        new CanvasGeometry?[FineStrandGeometryGroupCount];
    private CanvasLinearGradientBrush? _verticalOpacityMask;
    private CanvasGeometry? _cachedLens;
    private CanvasGeometry? _cachedLensOutline;
    private LensCacheKey _lensCacheKey;
    private double _lastFrameSeconds;
    private double _lastRenderRequestSeconds = double.NegativeInfinity;
    private double _nextTargetSeconds;
    private double _notificationStartedAt = double.NegativeInfinity;
    private double _notificationTimingScale = 1;
    private int _notificationTravelDirection = 1;
    private double _lastNotificationIconProgress = -1;
    private double _lastNotificationExpansionProgress = -1;
    private double _expansionProgress;
    private double _intensity = 1;
    private float _orbAttachmentRadius = 19f;
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
        _notificationTravelDirection *= -1;
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
        _verticalOpacityMask?.Dispose();
        _verticalOpacityMask = null;
        DisposeFineStrandGeometries();
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
        var notificationAge = (seconds - _notificationStartedAt) / _notificationTimingScale;
        var isVisuallyActive = _isPlaying ||
                               _expansionProgress > 0.001 ||
                               notificationAge is >= 0 and <= 2.24;
        var presentationRate = isVisuallyActive
            ? ActivePresentationRate
            : IdlePresentationRate;
        if (seconds - _lastRenderRequestSeconds < 1d / presentationRate)
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
        Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _verticalOpacityMask?.Dispose();
        _verticalOpacityMask = null;
        InvalidateLensCache();
    }

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
        var notificationAge = (seconds - _notificationStartedAt) / _notificationTimingScale;
        var notificationProgress = NotificationLensProgress(notificationAge);
        PublishNotificationProgress(
            notificationProgress,
            NotificationIconProgress(notificationAge));
        if (!_windowController.HasNativeCollapsedSurface || _expansionProgress > 0.001)
        {
            DrawFluid(args.DrawingSession, sender, width, height, seconds, signal);
        }
        _windowController.RenderCollapsedEdge(new EdgeFluidFrame(
            _primaryPoints,
            _secondaryPoints,
            _interferencePoints,
            _filamentPoints,
            _fineStrandPoints,
            _accentBrush.Color,
            _orbScale,
            _pressureField,
            notificationProgress,
            _orbAttachmentRadius,
            (float)signal.Energy,
            seconds));
    }

    private void UpdateTargets(double seconds, EdgeMotionSignal signal)
    {
        var characterScale = _character switch
        {
            EdgeFluidCharacter.Calm => 0.66,
            EdgeFluidCharacter.Expressive => 1.24,
            _ => 1,
        };
        var activity = (0.26 + (signal.Energy * 1.34)) * _intensity * characterScale;

        for (var index = 0; index < ControlPointCount; index++)
        {
            var normalized = index / (double)(ControlPointCount - 1);
            var presence = VerticalPresence(normalized);

            // Every target samples a spatially coherent field. The springs still
            // interpolate sparse simulation updates, but adjacent nodes now
            // belong to one continuous fluid body rather than unrelated bends.
            var primaryBody = SignedNoise((seconds * 0.082) + (normalized * 1.34), 17);
            var primaryFold = SignedNoise((-seconds * 0.047) + (normalized * 3.12) + 0.71, 61);
            var primaryDetail = SignedNoise((seconds * 0.137) + (normalized * 5.26) + 1.63, 109);

            var secondaryBody = SignedNoise((-seconds * 0.069) + (normalized * 1.57) + 1.11, 149);
            var secondaryFold = SignedNoise((seconds * 0.041) + (normalized * 3.71) + 0.26, 211);
            var secondaryDetail = SignedNoise((-seconds * 0.121) + (normalized * 5.83) + 2.07, 277);

            var tertiaryBody = SignedNoise((seconds * 0.054) + (normalized * 1.18) + 2.19, 337);
            var tertiaryFold = SignedNoise((-seconds * 0.036) + (normalized * 2.84) + 1.37, 401);
            var tertiaryDetail = SignedNoise((seconds * 0.098) + (normalized * 4.67) + 0.43, 463);

            _primaryNodes[index].Target =
                ((primaryBody * 0.61) + (primaryFold * 0.28) + (primaryDetail * 0.11)) *
                activity * presence;
            _secondaryNodes[index].Target =
                ((secondaryBody * 0.63) + (secondaryFold * 0.27) + (secondaryDetail * 0.10)) *
                activity * presence * 0.88;
            _tertiaryNodes[index].Target =
                ((tertiaryBody * 0.68) + (tertiaryFold * 0.23) + (tertiaryDetail * 0.09)) *
                activity * presence * 0.72;
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
        var stiffness = _isPlaying ? 18.5 : 9.4;
        var damping = _isPlaying ? 8.6 : 6.15;
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
        var playingReach = _isPlaying ? 4.8f + ((float)signal.LowBand * 7.8f) : 0f;
        var expression = _character == EdgeFluidCharacter.Expressive ? 4.8f : 0f;
        var expansionReach = (float)(
            SmoothStep(_expansionProgress) * Math.Min(92, width * 0.23f));
        var notificationAge = (seconds - _notificationStartedAt) / _notificationTimingScale;
        var notificationLens = NotificationLensProgress(notificationAge);
        var idleOrbHeight = _orbScale == EdgeOrbScale.Small ? 31f : 38f;
        var activeOrbRadius = (idleOrbHeight + ((float)notificationLens * 12f)) / 2f;
        _orbAttachmentRadius = activeOrbRadius;
        var orbRadiusNormalized = activeOrbRadius / Math.Max(1f, height);
        var connectorLength = Math.Clamp(72f / Math.Max(1f, height), 0.072, 0.125);

        for (var index = 0; index < RenderPointCount; index++)
        {
            var normalized = index / (double)(RenderPointCount - 1);
            var presence = VerticalPresence(normalized);
            var centerField = Gaussian(normalized, 0.5, 2.05);
            var centerCore = Gaussian(normalized, 0.5, 5.8);
            var shoulders =
                Gaussian(normalized, 0.455, 21) +
                Gaussian(normalized, 0.545, 21);
            var distantFlow =
                Gaussian(normalized, 0.305, 7.4) +
                Gaussian(normalized, 0.695, 7.4);
            var centerDistance = Math.Abs(normalized - 0.5);
            var distanceFromOrb = Math.Max(0, centerDistance - orbRadiusNormalized);
            var attachmentProgress = Math.Clamp(distanceFromOrb / connectorLength, 0, 1);
            var attachmentGate = SmootherStep(attachmentProgress);
            var attachmentShoulder = Gaussian(
                centerDistance,
                orbRadiusNormalized + (connectorLength * 0.78),
                10.8);
            var baseReach = presence *
                            (2.1 +
                             ((14.2 + (playingReach * 0.52)) * centerField) +
                             ((19.4 + (playingReach * 0.76) + expression) * centerCore) +
                             (6.5 * shoulders) +
                             (3.6 * distantFlow) +
                             (7.2 * attachmentShoulder));
            var primaryDrift = SampleNode(_primaryNodes, normalized) *
                               (3.7 + (signal.MidBand * 3.8));
            var secondaryDrift = SampleNode(_secondaryNodes, normalized) *
                                 (3.1 + (signal.HighBand * 3.2));
            var tertiaryDrift = SampleNode(_tertiaryNodes, normalized) *
                                (2.4 + (signal.MidBand * 2.5));
            var fieldDrift =
                ((SignedNoise((seconds * 0.046) + (normalized * 1.83), 521) * 0.68) +
                 (SignedNoise((-seconds * 0.031) + (normalized * 3.46) + 1.7, 577) * 0.32)) *
                presence * (1.4 + (centerField * 1.35));
            var notification = NotificationDisplacement(
                normalized,
                notificationAge,
                _notificationTravelDirection);
            var expansion = expansionReach * Gaussian(normalized, 0.5, 2.12);
            var y = (float)(normalized * height);

            // Four structural depths form the quiet body of the field. They do
            // not track each other exactly, so the silhouette reads as pressure
            // and refraction instead of parallel wire.
            var primaryReach = Math.Clamp(attachmentGate * (
                (baseReach * 1.18) + (primaryDrift * 0.78) + fieldDrift +
                notification + expansion),
                0.15,
                width - 2);
            var secondaryReach = Math.Clamp(attachmentGate * (
                (baseReach * 0.78) + (secondaryDrift * 0.82) - (fieldDrift * 0.24) +
                (notification * 0.66) + (expansion * 0.88)),
                0.1,
                width - 2);
            var interferenceReach = Math.Clamp(attachmentGate * (
                (baseReach * 1.52) - (primaryDrift * 0.22) + (secondaryDrift * 0.42) +
                (fieldDrift * 0.35) + (notification * 0.84) + expansion),
                0.1,
                width - 2);
            var filamentReach = Math.Clamp(attachmentGate * (
                (baseReach * 0.28) + (tertiaryDrift * 0.38) + (secondaryDrift * 0.12) +
                (notification * 0.28) + (expansion * 0.72)),
                0.08,
                width - 2);

            _primaryPoints[index] = new Vector2(edgeX + (direction * (float)primaryReach), y);
            _secondaryPoints[index] = new Vector2(edgeX + (direction * (float)secondaryReach), y);
            _interferencePoints[index] = new Vector2(edgeX + (direction * (float)interferenceReach), y);
            _filamentPoints[index] = new Vector2(edgeX + (direction * (float)filamentReach), y);

            for (var strandIndex = 0; strandIndex < FineStrandCount; strandIndex++)
            {
                var lane = (strandIndex + 0.5) / FineStrandCount;
                var laneWeight = 4 * lane * (1 - lane);
                var family = strandIndex % 4;
                var familyFlow = SignedNoise(
                    (seconds * (0.032 + (family * 0.003))) +
                    (normalized * (1.32 + (family * 0.18))) +
                    (family * 0.71),
                    613 + (family * 97));
                var secondaryFlow = SignedNoise(
                    (-seconds * (0.024 + (lane * 0.008))) +
                    (normalized * (1.58 + (lane * 0.32))) +
                    (lane * 3.7),
                    997);
                // Lane order is deliberately invariant. Animation moves the
                // shared field around the strands; it never reorders adjacent
                // filaments, which prevents crossings and small hook-shaped
                // kinks close to the attachment point.
                var laneBlend = (lane + SmootherStep(lane)) * 0.5;
                var drift = Lerp(tertiaryDrift, primaryDrift, laneBlend) +
                            (secondaryDrift * laneWeight * 0.24);
                var innerReach =
                    (baseReach * 0.17) + (tertiaryDrift * 0.20) +
                    (notification * 0.22) + (expansion * 0.70);
                var outerReach =
                    (baseReach * 1.58) + (primaryDrift * 0.31) +
                    (fieldDrift * 0.48) + (notification * 0.88) + expansion;
                var fieldFold =
                    ((familyFlow * 0.64) + (secondaryFlow * 0.36)) *
                    presence * (0.24 + (centerField * 1.36)) *
                    (0.38 + (laneWeight * 0.62)) * attachmentGate;
                var shoulderPressure = attachmentShoulder *
                                       (1.4 + (laneWeight * 5.8) + (signal.Energy * 1.2));
                var strandReach = Math.Clamp(attachmentGate * (
                    Lerp(innerReach, outerReach, laneBlend) +
                    (drift * (0.26 + (laneWeight * 0.26))) +
                    fieldFold +
                    shoulderPressure +
                    (notification * laneWeight * 0.16)),
                    0.08,
                    width - 2);
                var microLift = SignedNoise(
                    (seconds * 0.041) +
                    (normalized * 2.42) +
                    (lane * 2.9),
                    1481 + (family * 31)) * presence * attachmentGate *
                    (0.10 + (signal.HighBand * 0.18));
                var strandY = Math.Clamp(y + (float)microLift, 0, height);
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
        var fieldScale = _pressureField == EdgePressureField.Airy ? 0.86f : 1.12f;

        // Convert each moving contour into a smooth cubic path once per frame,
        // then reuse it for every optical depth. The former segment renderer
        // issued thousands of DrawLine calls and made higher sample counts far
        // too expensive for an always-on surface.
        using var primaryGeometry = CreateSmoothContourGeometry(canvas, _primaryPoints);
        using var secondaryGeometry = CreateSmoothContourGeometry(canvas, _secondaryPoints);
        using var interferenceGeometry = CreateSmoothContourGeometry(canvas, _interferencePoints);
        using var filamentGeometry = CreateSmoothContourGeometry(canvas, _filamentPoints);
        try
        {
            for (var group = 0; group < FineStrandGeometryGroupCount; group++)
            {
                _fineStrandGeometries[group] = CreateFineStrandGeometry(canvas, group);
            }

            var opacityMask = GetVerticalOpacityMask(canvas, height);
            using (drawingSession.CreateLayer(opacityMask))
            {
                // Broad, almost subliminal strokes form translucent ribbon
                // bodies. No closed fill is used, so the desktop remains the
                // actual background of the collapsed field.
                DrawContourGeometry(drawingSession, interferenceGeometry, accent, 32f, (byte)(5 * fieldScale));
                DrawContourGeometry(drawingSession, primaryGeometry, accent, 22f, (byte)(8 * fieldScale));
                DrawContourGeometry(drawingSession, secondaryGeometry, accent, 15f, (byte)(7 * fieldScale));
                DrawContourGeometry(drawingSession, filamentGeometry, accent, 9f, (byte)(5 * fieldScale));
                DrawContourGeometry(
                    drawingSession,
                    primaryGeometry,
                    accent,
                    9.5f,
                    (byte)((11 + (energy * 3)) * fieldScale));

                DrawContourGeometry(drawingSession, interferenceGeometry, accent, 1.08f, 25);
                DrawContourGeometry(
                    drawingSession,
                    primaryGeometry,
                    accent,
                    0.92f,
                    (byte)(50 + (energy * 12)));
                DrawContourGeometry(drawingSession, secondaryGeometry, accent, 0.68f, 34);
                DrawContourGeometry(drawingSession, filamentGeometry, accent, 0.48f, 38);

                for (var group = 0; group < FineStrandGeometryGroupCount; group++)
                {
                    var isOpticalHighlight = group == FineStrandGeometryGroupCount - 1;
                    var centerEmphasis = isOpticalHighlight ? 0.74f : group / 4f;
                    var strandColor = isOpticalHighlight
                        ? Color.FromArgb(255, 244, 248, 252)
                        : accent;
                    var alpha = (byte)Math.Clamp(
                        (int)Math.Round(
                            ((isOpticalHighlight ? 38 : 17) +
                             (centerEmphasis * (isOpticalHighlight ? 28 : 30)) +
                             (energy * 9)) * fieldScale),
                        7,
                        72);
                    var widthScale = (isOpticalHighlight ? 0.42f : 0.30f) +
                                     (centerEmphasis * (isOpticalHighlight ? 0.22f : 0.25f));
                    DrawContourGeometry(
                        drawingSession,
                        _fineStrandGeometries[group]!,
                        strandColor,
                        widthScale,
                        alpha);
                }

                DrawContourGeometry(
                    drawingSession,
                    primaryGeometry,
                    Color.FromArgb(255, 246, 249, 252),
                    0.32f,
                    22);
                DrawContourGeometry(
                    drawingSession,
                    filamentGeometry,
                    Color.FromArgb(255, 246, 250, 253),
                    0.40f,
                    34);
            }
        }
        finally
        {
            DisposeFineStrandGeometries();
        }

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
            drawingSession.DrawGeometry(outline, Color.FromArgb(14, accent.R, accent.G, accent.B), 13f);
            drawingSession.DrawGeometry(outline, Color.FromArgb(30, accent.R, accent.G, accent.B), 4.6f);
            drawingSession.FillGeometry(
                shell,
                Color.FromArgb((byte)Lerp(16, 142, (float)expansion), 7, 11, 17));
            drawingSession.FillGeometry(
                shell,
                Color.FromArgb((byte)Lerp(25, 7, (float)expansion), accent.R, accent.G, accent.B));
            drawingSession.DrawGeometry(
                outline,
                Color.FromArgb((byte)Lerp(108, 53, (float)expansion), 239, 244, 248),
                0.68f);
            drawingSession.DrawGeometry(
                outline,
                Color.FromArgb((byte)Lerp(62, 30, (float)expansion), accent.R, accent.G, accent.B),
                1.18f);

            if (expansion < 0.58)
            {
                DrawLensOptics(
                    drawingSession,
                    width,
                    centerY,
                    visibleReach,
                    lensHeight,
                    seconds,
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
        var expansionEndpointChanged =
            expansionProgress is 0 or 1 &&
            expansionProgress != _lastNotificationExpansionProgress;
        if (Math.Abs(expansionProgress - _lastNotificationExpansionProgress) > 0.001 ||
            expansionEndpointChanged)
        {
            _lastNotificationExpansionProgress = expansionProgress;
            NotificationExpansionProgressChanged?.Invoke(expansionProgress);
        }

        var iconEndpointChanged =
            iconProgress is 0 or 1 &&
            iconProgress != _lastNotificationIconProgress;
        if (Math.Abs(iconProgress - _lastNotificationIconProgress) > 0.001 || iconEndpointChanged)
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
        double seconds,
        Color accent,
        float opacity,
        float energy)
    {
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var edgeX = _side == EdgeSide.Right ? width : 0f;
        var opticalEnergy = Math.Clamp(energy, 0f, 1f);
        // The shell center never moves. Breathing is optical-only and remains
        // below a quarter DIP at the normal orb size, avoiding the previous
        // impression that the interaction target itself was jittering.
        var breathing = 1f +
                        ((float)Math.Sin(seconds * 0.47) * 0.006f) +
                        ((float)Math.Sin((seconds * 0.21) + 1.34) * 0.0035f);
        var radius = lensHeight * 0.41f * breathing;
        var center = new Vector2(edgeX, centerY);

        drawingSession.FillCircle(
            center,
            radius * 2.35f,
            Color.FromArgb((byte)(4 * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            center,
            radius * 1.62f,
            Color.FromArgb((byte)(7 * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            center,
            radius,
            Color.FromArgb((byte)((11 + (opticalEnergy * 3)) * opacity), 246, 249, 252));
        drawingSession.FillCircle(
            center,
            radius * 0.91f,
            Color.FromArgb((byte)(18 * opacity), 3, 7, 13));
        drawingSession.FillCircle(
            center,
            radius * 0.78f,
            Color.FromArgb(
                (byte)((26 + (opticalEnergy * 12)) * opacity),
                accent.R,
                accent.G,
                accent.B));
        drawingSession.DrawCircle(
            center,
            radius * 0.94f,
            Color.FromArgb((byte)(68 * opacity), 247, 250, 253),
            0.52f);
        drawingSession.DrawCircle(
            center,
            radius * 0.72f,
            Color.FromArgb((byte)(46 * opacity), accent.R, accent.G, accent.B),
            0.38f);

        // Nested eccentric ellipses evoke refractive filament glass without
        // introducing another animated center point. They remain deliberately
        // quiet and are clipped by the monitor boundary into a true half-orb.
        for (var index = 0; index < 8; index++)
        {
            var normalized = index / 7f;
            var meshColor = index is 2 or 6
                ? Color.FromArgb((byte)((20 + ((1 - normalized) * 16)) * opacity), 246, 250, 253)
                : Color.FromArgb(
                    (byte)((16 + ((1 - Math.Abs((normalized * 2) - 1)) * 20)) * opacity),
                    accent.R,
                    accent.G,
                    accent.B);
            drawingSession.DrawEllipse(
                center,
                radius * (0.28f + (normalized * 0.62f)),
                radius * (0.49f + (normalized * 0.45f)),
                meshColor,
                0.24f + ((1 - normalized) * 0.08f));
        }

        drawingSession.FillCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.62f), centerY - (lensHeight * 0.11f)),
            Math.Max(2.2f, visibleReach * 0.20f),
            Color.FromArgb((byte)((34 + (opticalEnergy * 12)) * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.73f), centerY - (lensHeight * 0.24f)),
            Math.Max(0.8f, visibleReach * 0.062f),
            Color.FromArgb((byte)(184 * opacity), 250, 252, 255));

        var top = centerY - (lensHeight * 0.39f);
        drawingSession.DrawLine(
            new Vector2(edgeX + (direction * visibleReach * 0.24f), top),
            new Vector2(
                edgeX + (direction * visibleReach * 0.78f),
                centerY - (lensHeight * 0.19f)),
            Color.FromArgb((byte)(148 * opacity), 250, 252, 255),
            0.72f,
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

    private CanvasGeometry CreateSmoothContourGeometry(
        CanvasControl canvas,
        Vector2[] points)
    {
        using var builder = new CanvasPathBuilder(canvas);
        AppendVisibleArms(builder, points);
        return CanvasGeometry.CreatePath(builder);
    }

    private CanvasGeometry CreateFineStrandGeometry(CanvasControl canvas, int group)
    {
        using var builder = new CanvasPathBuilder(canvas);
        for (var strandIndex = 0; strandIndex < FineStrandCount; strandIndex++)
        {
            if (FineStrandGeometryGroup(strandIndex) == group)
            {
                AppendVisibleArms(builder, _fineStrandPoints[strandIndex]);
            }
        }

        return CanvasGeometry.CreatePath(builder);
    }

    private void AppendVisibleArms(CanvasPathBuilder builder, Vector2[] points)
    {
        var centerY = _canvas.ActualHeight / 2f;
        var upperEnd = -1;
        var lowerStart = points.Length;
        for (var index = 0; index < points.Length; index++)
        {
            if (points[index].Y <= centerY - _orbAttachmentRadius)
            {
                upperEnd = index;
            }

            if (lowerStart == points.Length &&
                points[index].Y >= centerY + _orbAttachmentRadius)
            {
                lowerStart = index;
            }
        }

        AppendSmoothFigure(builder, points, 0, upperEnd);
        AppendSmoothFigure(builder, points, lowerStart, points.Length - 1);
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
                Lerp(current.Y, next.Y, 1f / 3f));
            var controlTwo = new Vector2(
                Math.Clamp(next.X - ((following.X - current.X) / 6f), minimumX, maximumX),
                Lerp(current.Y, next.Y, 2f / 3f));
            builder.AddCubicBezier(controlOne, controlTwo, next);
        }

        builder.EndFigure(CanvasFigureLoop.Open);
    }

    private CanvasLinearGradientBrush GetVerticalOpacityMask(CanvasControl canvas, float height)
    {
        _verticalOpacityMask ??= new CanvasLinearGradientBrush(
            canvas,
            [
                new CanvasGradientStop { Position = 0f, Color = Color.FromArgb(0, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.065f, Color = Color.FromArgb(14, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.19f, Color = Color.FromArgb(90, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.34f, Color = Color.FromArgb(210, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.46f, Color = Color.FromArgb(255, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.54f, Color = Color.FromArgb(255, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.66f, Color = Color.FromArgb(210, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.81f, Color = Color.FromArgb(90, 255, 255, 255) },
                new CanvasGradientStop { Position = 0.935f, Color = Color.FromArgb(14, 255, 255, 255) },
                new CanvasGradientStop { Position = 1f, Color = Color.FromArgb(0, 255, 255, 255) },
            ]);
        _verticalOpacityMask.StartPoint = Vector2.Zero;
        _verticalOpacityMask.EndPoint = new Vector2(0, height);
        return _verticalOpacityMask;
    }

    private void DrawContourGeometry(
        CanvasDrawingSession drawingSession,
        CanvasGeometry geometry,
        Color color,
        float strokeWidth,
        byte alpha)
    {
        drawingSession.DrawGeometry(
            geometry,
            Color.FromArgb(alpha, color.R, color.G, color.B),
            strokeWidth,
            _roundedStroke);
    }

    private void DisposeFineStrandGeometries()
    {
        for (var index = 0; index < _fineStrandGeometries.Length; index++)
        {
            _fineStrandGeometries[index]?.Dispose();
            _fineStrandGeometries[index] = null;
        }
    }

    private static int FineStrandGeometryGroup(int strandIndex)
    {
        if (IsOpticalHighlight(strandIndex))
        {
            return FineStrandGeometryGroupCount - 1;
        }

        var normalizedLane = strandIndex / (float)(FineStrandCount - 1);
        var centerEmphasis = MathF.Sin(normalizedLane * MathF.PI);
        return Math.Clamp((int)MathF.Round(centerEmphasis * 4f), 0, 4);
    }

    private double VerticalPresence(double normalized)
    {
        var fadeDistance = _verticalReach == EdgeVerticalReach.Extended ? 0.075 : 0.135;
        var edgeFade = SmootherStep(Math.Clamp(normalized / fadeDistance, 0, 1)) *
                       SmootherStep(Math.Clamp((1 - normalized) / fadeDistance, 0, 1));
        var centerSharpness = _verticalReach == EdgeVerticalReach.Extended ? 2.16 : 2.68;
        var broadCenter = Gaussian(normalized, 0.5, centerSharpness);
        var floor = _verticalReach == EdgeVerticalReach.Extended ? 0.085 : 0.055;
        return edgeFade * (floor + ((1 - floor) * broadCenter));
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

    private static double NotificationDisplacement(
        double normalizedY,
        double age,
        int travelDirection)
    {
        if (age is < 0 or > 2.24)
        {
            return 0;
        }

        // One pressure packet approaches the lens, is absorbed, and becomes a
        // pair of broad outgoing ripples. Each phase overlaps the next with
        // zero-velocity easing, so there is no visible hand-off or sudden pop.
        var incomingProgress = Math.Clamp(age / 0.66, 0, 1);
        var source = travelDirection >= 0 ? 0.14 : 0.86;
        var incomingCenter = Lerp(source, 0.5, SmootherStep(incomingProgress));
        var incomingEnvelope =
            SmootherStep(Math.Clamp(age / 0.10, 0, 1)) *
            (1 - SmootherStep(Math.Clamp((age - 0.48) / 0.30, 0, 1)));
        var incoming = Gaussian(normalizedY, incomingCenter, 27) * incomingEnvelope * 11.8;

        var impactEnvelope =
            SmootherStep(Math.Clamp((age - 0.48) / 0.18, 0, 1)) *
            (1 - SmootherStep(Math.Clamp((age - 0.78) / 0.30, 0, 1)));
        var impact = Gaussian(normalizedY, 0.5, 16.5) * impactEnvelope * 5.4;

        var rippleProgress = Math.Clamp((age - 0.64) / 1.60, 0, 1);
        var radius = SmootherStep(rippleProgress) * 0.49;
        var distance = Math.Abs(normalizedY - 0.5);
        var rippleEnvelope =
            SmootherStep(Math.Clamp((age - 0.64) / 0.14, 0, 1)) *
            (1 - SmootherStep(Math.Clamp((age - 1.62) / 0.62, 0, 1)));
        var leading = Math.Exp(-Math.Pow((distance - radius) * 23, 2)) *
                      rippleEnvelope * 11.6;
        var trailingRadius = Math.Max(0, radius - 0.052);
        var trailing = -Math.Exp(-Math.Pow((distance - trailingRadius) * 19, 2)) *
                       rippleEnvelope * 3.1;
        return incoming + impact + leading + trailing;
    }

    private static double NotificationLensProgress(double age)
    {
        if (age is < 0.46 or > 2.16)
        {
            return 0;
        }

        if (age < 0.78)
        {
            return SmootherStep((age - 0.46) / 0.32);
        }

        return age < 1.40 ? 1 : 1 - SmootherStep((age - 1.40) / 0.76);
    }

    private static double NotificationIconProgress(double age)
    {
        if (age is < 0.62 or > 1.98)
        {
            return 0;
        }

        if (age < 0.86)
        {
            return SmootherStep((age - 0.62) / 0.24);
        }

        return age < 1.42 ? 1 : 1 - SmootherStep((age - 1.42) / 0.56);
    }

    private static bool IsOpticalHighlight(int strandIndex) =>
        strandIndex is 6 or 15 or 25;

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
