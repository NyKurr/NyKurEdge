using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.App.Presentation.Animations;
using NyKurEdge.Core.Settings;
using Windows.UI;

namespace NyKurEdge.App.Presentation.Edge;

public enum EdgeFluidCharacter { Calm, Balanced, Expressive }

public enum EdgeOrbScale { Small, Medium }

public enum EdgeShellShape { SoftCapsule, TaperedBloom }

/// <summary>
/// Draws one filled pressure field, its subordinate traces, and the embedded
/// glass lens that grows into the expanded shell. Motion targets update at a
/// low rate while the compositor crossfades cached surfaces at display cadence.
/// </summary>
public sealed class EdgeWaveRenderer : IDisposable
{
    private const int ControlPointCount = 11;
    private const int RenderPointCount = 49;
    private const double TargetIntervalSeconds = 0.125;

    private readonly CanvasControl[] _canvases;
    private readonly DispatcherQueueTimer _frameTimer;
    private readonly SolidColorBrush _accentBrush;
    private readonly IEdgeMotionSource _motionSource;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly FluidNode[] _primaryNodes = new FluidNode[ControlPointCount];
    private readonly FluidNode[] _secondaryNodes = new FluidNode[ControlPointCount];
    private readonly Vector2[] _primaryPoints = new Vector2[RenderPointCount];
    private readonly Vector2[] _secondaryPoints = new Vector2[RenderPointCount];
    private readonly Vector2[] _innerPoints = new Vector2[RenderPointCount];
    private CanvasGeometry? _cachedLens;
    private CanvasGeometry? _cachedLensOutline;
    private LensCacheKey _lensCacheKey;
    private double _lastFrameSeconds;
    private double _nextTargetSeconds;
    private double _notificationStartedAt = double.NegativeInfinity;
    private double _notificationTimingScale = 1;
    private double _lastNotificationIconProgress = -1;
    private double _expansionProgress;
    private double _intensity = 1;
    private int _targetEpoch;
    private int _visibleCanvasIndex;
    private bool _isPlaying;
    private bool _isRunning;
    private bool _hasInitialFrame;
    private bool _disposed;
    private EdgeSide _side = EdgeSide.Right;
    private EdgeFluidCharacter _character = EdgeFluidCharacter.Balanced;
    private EdgeOrbScale _orbScale = EdgeOrbScale.Medium;
    private EdgeShellShape _shellShape = EdgeShellShape.SoftCapsule;

    public EdgeWaveRenderer(
        CanvasControl primaryCanvas,
        CanvasControl secondaryCanvas,
        SolidColorBrush accentBrush,
        IEdgeMotionSource? motionSource = null)
    {
        _canvases = [primaryCanvas, secondaryCanvas];
        _accentBrush = accentBrush;
        _motionSource = motionSource ?? new ProceduralEdgeMotionSource();
        foreach (var canvas in _canvases)
        {
            canvas.UseSharedDevice = true;
            canvas.ClearColor = Color.FromArgb(0, 0, 0, 0);
            canvas.CreateResources += OnCreateResources;
            canvas.Draw += OnDraw;
            canvas.SizeChanged += OnSizeChanged;
        }

        ElementCompositionPreview.GetElementVisual(_canvases[0]).Opacity = 1;
        ElementCompositionPreview.GetElementVisual(_canvases[1]).Opacity = 0;

        _frameTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _frameTimer.IsRepeating = true;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(240);
        _frameTimer.Tick += OnFrameTimerTick;
    }

    public EdgeFluidCharacter Character => _character;

    public EdgeOrbScale OrbScale => _orbScale;

    public EdgeShellShape ShellShape => _shellShape;

    public event Action<double>? NotificationIconProgressChanged;

    public void Start()
    {
        ThrowIfDisposed();
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _lastFrameSeconds = _clock.Elapsed.TotalSeconds;
        UpdateFrameTimerInterval();
        _frameTimer.Start();
        _canvases[_visibleCanvasIndex].Invalidate();
    }

    public void Stop()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _frameTimer.Stop();
    }

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        UpdateFrameTimerInterval();
        InvalidateVisibleCanvas();
    }

    public void SetIntensity(AnimationIntensity intensity)
    {
        _intensity = intensity switch
        {
            AnimationIntensity.Quiet => 0.72,
            AnimationIntensity.Expressive => 1.22,
            _ => 1,
        };
        InvalidateVisibleCanvas();
    }

    public void SetSide(EdgeSide side)
    {
        _side = side;
        InvalidateLensCache();
        InvalidateVisibleCanvas();
    }

    public void SetExpansionProgress(double progress)
    {
        _expansionProgress = Math.Clamp(progress, 0, 1);
        InvalidateVisibleCanvas();
    }

    public void TriggerNotificationPulse(double timingScale = 1)
    {
        _notificationTimingScale = Math.Clamp(timingScale, 1, 4);
        _notificationStartedAt = _clock.Elapsed.TotalSeconds;
        UpdateFrameTimerInterval();
        InvalidateVisibleCanvas();
    }

    public EdgeFluidCharacter CycleCharacter()
    {
        _character = _character switch
        {
            EdgeFluidCharacter.Calm => EdgeFluidCharacter.Balanced,
            EdgeFluidCharacter.Balanced => EdgeFluidCharacter.Expressive,
            _ => EdgeFluidCharacter.Calm,
        };
        InvalidateVisibleCanvas();
        return _character;
    }

    public EdgeOrbScale CycleOrbScale()
    {
        _orbScale = _orbScale == EdgeOrbScale.Small ? EdgeOrbScale.Medium : EdgeOrbScale.Small;
        InvalidateLensCache();
        InvalidateVisibleCanvas();
        return _orbScale;
    }

    public EdgeShellShape CycleShellShape()
    {
        _shellShape = _shellShape == EdgeShellShape.SoftCapsule
            ? EdgeShellShape.TaperedBloom
            : EdgeShellShape.SoftCapsule;
        InvalidateLensCache();
        InvalidateVisibleCanvas();
        return _shellShape;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _frameTimer.Tick -= OnFrameTimerTick;
        foreach (var canvas in _canvases)
        {
            canvas.CreateResources -= OnCreateResources;
            canvas.Draw -= OnDraw;
            canvas.SizeChanged -= OnSizeChanged;
            canvas.RemoveFromVisualTree();
        }
        InvalidateLensCache();
        _clock.Stop();
    }

    private void OnFrameTimerTick(DispatcherQueueTimer sender, object args)
    {
        UpdateFrameTimerInterval();
        if (_expansionProgress is > 0.001 and < 0.999)
        {
            return;
        }

        var targetCanvasIndex = 1 - _visibleCanvasIndex;
        _canvases[targetCanvasIndex].Invalidate();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateLensCache();
        if (ReferenceEquals(sender, _canvases[_visibleCanvasIndex]))
        {
            InvalidateVisibleCanvas();
        }
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
        var deltaSeconds = Math.Clamp(seconds - _lastFrameSeconds, 0, 0.25);
        _lastFrameSeconds = seconds;
        var signal = _motionSource.Sample(seconds, _isPlaying).Normalize();

        if (seconds >= _nextTargetSeconds)
        {
            UpdateTargets(signal);
            _nextTargetSeconds = seconds + TargetIntervalSeconds;
        }

        while (deltaSeconds > 0)
        {
            var step = Math.Min(deltaSeconds, 0.025);
            IntegrateNodes(step);
            deltaSeconds -= step;
        }
        BuildContours(width, height, seconds, signal);
        DrawFluid(args.DrawingSession, sender, width, height, seconds, signal);

        var drawnCanvasIndex = ReferenceEquals(sender, _canvases[0]) ? 0 : 1;
        if (!_hasInitialFrame)
        {
            if (drawnCanvasIndex == 0)
            {
                _hasInitialFrame = true;
                _visibleCanvasIndex = 0;
            }
            return;
        }

        if (_isRunning && drawnCanvasIndex != _visibleCanvasIndex)
        {
            CrossFadeTo(drawnCanvasIndex);
        }
    }

    private void InvalidateVisibleCanvas()
    {
        if (!_disposed)
        {
            _canvases[_visibleCanvasIndex].Invalidate();
        }
    }

    private void UpdateFrameTimerInterval()
    {
        var notificationActive =
            (_clock.Elapsed.TotalSeconds - _notificationStartedAt) <
            (1.9 * _notificationTimingScale);
        _frameTimer.Interval = TimeSpan.FromMilliseconds(
            notificationActive
                ? 40
                : _isPlaying
                    ? 100
                    : 240);
    }

    private void CrossFadeTo(int targetCanvasIndex)
    {
        var sourceVisual = ElementCompositionPreview.GetElementVisual(_canvases[_visibleCanvasIndex]);
        var targetVisual = ElementCompositionPreview.GetElementVisual(_canvases[targetCanvasIndex]);
        var duration = TimeSpan.FromTicks((long)(_frameTimer.Interval.Ticks * 0.92));

        StartOpacityAnimation(sourceVisual, 1, 0, duration);
        StartOpacityAnimation(targetVisual, 0, 1, duration);
        _visibleCanvasIndex = targetCanvasIndex;
    }

    private static void StartOpacityAnimation(
        Visual visual,
        float from,
        float to,
        TimeSpan duration)
    {
        visual.StopAnimation("Opacity");
        visual.Opacity = to;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, from);
        animation.InsertKeyFrame(1, to);
        animation.Duration = duration;
        visual.StartAnimation("Opacity", animation);
    }

    private void UpdateTargets(EdgeMotionSignal signal)
    {
        var characterScale = _character switch
        {
            EdgeFluidCharacter.Calm => 0.66,
            EdgeFluidCharacter.Expressive => 1.28,
            _ => 1,
        };
        var activity = (0.48 + (signal.Energy * 1.55)) * _intensity * characterScale;

        for (var index = 0; index < ControlPointCount; index++)
        {
            var normalized = index / (double)(ControlPointCount - 1);
            var envelope = Gaussian(normalized, 0.5, 3.15);
            _primaryNodes[index].Target = SignedHash(_targetEpoch, index, 17) * activity * envelope;
            _secondaryNodes[index].Target = SignedHash(_targetEpoch, index, 73) * activity * envelope * 0.72;
        }

        _targetEpoch++;
    }

    private void IntegrateNodes(double deltaSeconds)
    {
        var responsiveness = _isPlaying ? 11.8 : 7.8;
        var damping = _isPlaying ? 6.8 : 7.3;
        for (var index = 0; index < ControlPointCount; index++)
        {
            Integrate(ref _primaryNodes[index], deltaSeconds, responsiveness, damping);
            Integrate(ref _secondaryNodes[index], deltaSeconds, responsiveness * 0.86, damping * 1.03);
        }
    }

    private void BuildContours(float width, float height, double seconds, EdgeMotionSignal signal)
    {
        var edgeX = _side == EdgeSide.Right ? width : 0f;
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var playingReach = _isPlaying ? 6f + ((float)signal.LowBand * 7f) : 0f;
        var expression = _character == EdgeFluidCharacter.Expressive ? 5f : 0f;
        var expansionReach = (float)(SmoothStep(_expansionProgress) * 55);
        var notificationAge = seconds - _notificationStartedAt;
        notificationAge /= _notificationTimingScale;

        for (var index = 0; index < RenderPointCount; index++)
        {
            var normalized = index / (double)(RenderPointCount - 1);
            var centerEnvelope = Gaussian(normalized, 0.5, 3.25);
            var orbChannel = Gaussian(normalized, 0.5, 17.2);
            var shoulders = Gaussian(normalized, 0.422, 24) + Gaussian(normalized, 0.578, 24);
            var baseReach = 0.55 +
                            (centerEnvelope * (34 + playingReach + expression) * (1 - (orbChannel * 0.84))) +
                            (shoulders * 9.5) +
                            (expansionReach * Gaussian(normalized, 0.5, 2.25));
            var primaryNoise = SampleNode(_primaryNodes, normalized) *
                               (2.5 + (signal.MidBand * 5.2)) * centerEnvelope;
            var secondaryNoise = SampleNode(_secondaryNodes, normalized) *
                                 (2 + (signal.HighBand * 3.8)) * centerEnvelope;
            var notification = NotificationDisplacement(normalized, notificationAge);
            var y = (float)(normalized * height);

            var primaryReach = Math.Clamp(baseReach + primaryNoise + notification, 0.35, width - 2);
            var secondaryReach = Math.Clamp(
                (baseReach * 0.62) + secondaryNoise + (notification * 0.56), 0.2, width - 2);
            var innerReach = Math.Clamp(
                (baseReach * 0.33) + (primaryNoise * 0.24) + (notification * 0.28), 0.1, width - 2);

            _primaryPoints[index] = new Vector2(edgeX + (direction * (float)primaryReach), y);
            _secondaryPoints[index] = new Vector2(edgeX + (direction * (float)secondaryReach), y);
            _innerPoints[index] = new Vector2(edgeX + (direction * (float)innerReach), y);
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
        var edgeX = _side == EdgeSide.Right ? width : 0f;

        using var pressureField = CreatePressureFieldGeometry(canvas, edgeX, height);
        drawingSession.FillGeometry(
            pressureField,
            Color.FromArgb((byte)(11 + (signal.Energy * 11)), accent.R, accent.G, accent.B));

        using var primaryBand = CreateBandGeometry(canvas, _primaryPoints, _secondaryPoints);
        drawingSession.FillGeometry(
            primaryBand,
            Color.FromArgb((byte)(13 + (signal.Energy * 8)), accent.R, accent.G, accent.B));

        DrawContour(drawingSession, _primaryPoints, accent, 12f, 8, height);
        DrawContour(drawingSession, _primaryPoints, accent, 5.5f, 18, height);
        DrawContour(drawingSession, _primaryPoints, accent, 1.48f, 132, height);
        DrawContour(drawingSession, _secondaryPoints, accent, 0.88f, 31, height);
        DrawContour(
            drawingSession,
            _innerPoints,
            Color.FromArgb(255, 255, 255, 255),
            0.68f,
            30,
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

    private CanvasGeometry CreatePressureFieldGeometry(CanvasControl canvas, float edgeX, float height)
    {
        using var builder = new CanvasPathBuilder(canvas);
        builder.BeginFigure(new Vector2(edgeX, 0));
        foreach (var point in _primaryPoints)
        {
            builder.AddLine(point);
        }

        builder.AddLine(new Vector2(edgeX, height));
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static CanvasGeometry CreateBandGeometry(
        CanvasControl canvas,
        Vector2[] outerPoints,
        Vector2[] innerPoints)
    {
        using var builder = new CanvasPathBuilder(canvas);
        builder.BeginFigure(outerPoints[0]);
        for (var index = 1; index < outerPoints.Length; index++)
        {
            builder.AddLine(outerPoints[index]);
        }

        for (var index = innerPoints.Length - 1; index >= 0; index--)
        {
            builder.AddLine(innerPoints[index]);
        }

        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
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
        var iconProgress = SmoothStep(Math.Clamp((notification - 0.18) / 0.72, 0, 1));
        if (Math.Abs(iconProgress - _lastNotificationIconProgress) > 0.012 || iconProgress is 0 or 1)
        {
            _lastNotificationIconProgress = iconProgress;
            NotificationIconProgressChanged?.Invoke(iconProgress);
        }

        var breathing = (float)signal.Energy * (_isPlaying ? 1f : 0.3f);
        var baseHeight = _orbScale == EdgeOrbScale.Small ? 30f : 36f;
        var baseReach = _orbScale == EdgeOrbScale.Small ? 15f : 20f;
        var targetHeight = _shellShape == EdgeShellShape.SoftCapsule ? 272f : 258f;
        var horizontalProgress = (float)(1 - Math.Pow(1 - expansion, 3.4));
        var verticalProgress = (float)Math.Pow(expansion, 0.66);
        var visibleReach = Lerp(
            baseReach + ((float)notification * 28f),
            Math.Max(baseReach, width - 10f),
            horizontalProgress);
        var lensHeight = Lerp(baseHeight + ((float)notification * 10f), targetHeight, verticalProgress);
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
            drawingSession.DrawGeometry(outline, Color.FromArgb(18, accent.R, accent.G, accent.B), 11f);
            drawingSession.DrawGeometry(outline, Color.FromArgb(28, accent.R, accent.G, accent.B), 4.5f);
            drawingSession.FillGeometry(shell, Color.FromArgb((byte)Lerp(76, 138, (float)expansion), 9, 13, 19));
            drawingSession.FillGeometry(
                shell,
                Color.FromArgb((byte)Lerp(21, 8, (float)expansion), accent.R, accent.G, accent.B));
            drawingSession.DrawGeometry(
                outline,
                Color.FromArgb((byte)Lerp(96, 53, (float)expansion), 236, 242, 248),
                0.82f);
            drawingSession.DrawGeometry(
                outline,
                Color.FromArgb((byte)Lerp(62, 31, (float)expansion), accent.R, accent.G, accent.B),
                1.45f);

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
                    breathing);
            }
        }
        finally
        {
            transientOutline?.Dispose();
            transientShell?.Dispose();
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
        float breathing)
    {
        var direction = _side == EdgeSide.Right ? -1f : 1f;
        var edgeX = _side == EdgeSide.Right ? width : 0f;

        var innerRadius = (lensHeight * 0.37f) + breathing;
        drawingSession.FillCircle(
            new Vector2(edgeX, centerY),
            innerRadius,
            Color.FromArgb((byte)(13 * opacity), 245, 249, 252));
        drawingSession.FillCircle(
            new Vector2(edgeX, centerY),
            innerRadius * 0.84f,
            Color.FromArgb((byte)((25 + (breathing * 4)) * opacity), accent.R, accent.G, accent.B));
        drawingSession.DrawCircle(
            new Vector2(edgeX, centerY),
            innerRadius * 0.88f,
            Color.FromArgb((byte)(37 * opacity), 247, 250, 253),
            0.58f);

        drawingSession.FillCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.62f), centerY - (lensHeight * 0.12f)),
            Math.Max(2.4f, visibleReach * 0.23f),
            Color.FromArgb((byte)(38 * opacity), accent.R, accent.G, accent.B));
        drawingSession.FillCircle(
            new Vector2(edgeX + (direction * visibleReach * 0.72f), centerY - (lensHeight * 0.24f)),
            Math.Max(0.8f, visibleReach * 0.065f),
            Color.FromArgb((byte)(196 * opacity), 250, 252, 255));

        var top = centerY - (lensHeight * 0.39f);
        drawingSession.DrawLine(
            new Vector2(edgeX + (direction * visibleReach * 0.25f), top),
            new Vector2(
                edgeX + (direction * visibleReach * 0.78f),
                centerY - (lensHeight * 0.19f)),
            Color.FromArgb((byte)(132 * opacity), 250, 252, 255),
            0.9f);
    }

    private void InvalidateLensCache()
    {
        _cachedLensOutline?.Dispose();
        _cachedLensOutline = null;
        _cachedLens?.Dispose();
        _cachedLens = null;
        _lensCacheKey = default;
    }

    private static void DrawContour(
        CanvasDrawingSession drawingSession,
        Vector2[] points,
        Color color,
        float strokeWidth,
        byte peakAlpha,
        float height)
    {
        var centerY = height / 2f;
        for (var index = 1; index < points.Length; index++)
        {
            var midpointY = (points[index - 1].Y + points[index].Y) / 2f;
            if (Math.Abs(midpointY - centerY) < 19f)
            {
                continue;
            }

            var envelope = Gaussian(midpointY / Math.Max(1f, height), 0.5, 3.2);
            var alpha = (byte)Math.Clamp((int)Math.Round(peakAlpha * envelope), 0, 255);
            if (alpha < 2)
            {
                continue;
            }

            drawingSession.DrawLine(
                points[index - 1],
                points[index],
                Color.FromArgb(alpha, color.R, color.G, color.B),
                strokeWidth);
        }
    }

    private static void Integrate(ref FluidNode node, double deltaSeconds, double responsiveness, double damping)
    {
        var acceleration = ((node.Target - node.Value) * responsiveness) - (node.Velocity * damping);
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
        if (age is < 0 or > 1.9)
        {
            return 0;
        }

        var incoming = age <= 0.38
            ? Gaussian(normalizedY, 0.16 + (SmootherStep(age / 0.38) * 0.34), 27) * 12
            : 0;
        if (age < 0.25)
        {
            return incoming;
        }

        var progress = Math.Clamp((age - 0.25) / 1.55, 0, 1);
        var radius = SmootherStep(progress) * 0.48;
        var ripple = Math.Exp(-Math.Pow((Math.Abs(normalizedY - 0.5) - radius) * 25, 2)) *
                     (1 - progress) * 16;
        return incoming + ripple;
    }

    private static double NotificationLensProgress(double age)
    {
        if (age is < 0 or > 1.82)
        {
            return 0;
        }

        if (age < 0.30)
        {
            return SmoothStep(age / 0.30);
        }

        return age < 1.18 ? 1 : 1 - SmoothStep((age - 1.18) / 0.64);
    }

    private static double SignedHash(int epoch, int index, int seed)
    {
        unchecked
        {
            var value = (uint)(epoch * 374761393 + index * 668265263 + seed * 2246822519);
            value = (value ^ (value >> 13)) * 1274126177;
            value ^= value >> 16;
            return ((value & 0x00FFFFFF) / (double)0x007FFFFF) - 1;
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
