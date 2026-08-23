using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.App.Presentation.Animations;
using NyKurEdge.Core.Settings;
using Windows.Foundation;
using XamlPath = Microsoft.UI.Xaml.Shapes.Path;

namespace NyKurEdge.App.Presentation.Edge;

public sealed class EdgeWaveRenderer : IDisposable
{
    private const int KnotCount = 13;
    private readonly FrameworkElement _host;
    private readonly WaveLayer[] _layers;
    private readonly IEdgeMotionSource _motionSource;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _notificationStartedAt = double.NegativeInfinity;
    private double _intensity = 1;
    private double _expansionProgress;
    private bool _isPlaying;
    private EdgeSide _side = EdgeSide.Right;

    public EdgeWaveRenderer(
        FrameworkElement host,
        XamlPath bloom,
        XamlPath outerTrace,
        XamlPath secondaryTrace,
        XamlPath coreTrace,
        IEdgeMotionSource? motionSource = null)
    {
        _host = host;
        _motionSource = motionSource ?? new ProceduralEdgeMotionSource();
        _layers =
        [
            new WaveLayer(
                [bloom, outerTrace],
                baseReach: 3.0,
                centerReach: 39,
                orbReach: 8,
                expansionReach: 48,
                amplitude: 1.0,
                phase: 1.05),
            new WaveLayer(
                secondaryTrace,
                baseReach: 2.2,
                centerReach: 27,
                orbReach: 6,
                expansionReach: 34,
                amplitude: 0.78,
                phase: 2.55),
            new WaveLayer(
                coreTrace,
                baseReach: 1.6,
                centerReach: 13,
                orbReach: 4,
                expansionReach: 18,
                amplitude: 0.54,
                phase: 3.35),
        ];

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _timer.Start();
        Render();
    }

    public void Stop() => _timer.Stop();

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        UpdateTimerInterval();
    }

    public void SetIntensity(AnimationIntensity intensity)
    {
        _intensity = intensity switch
        {
            AnimationIntensity.Quiet => 0.72,
            AnimationIntensity.Expressive => 1.22,
            _ => 1,
        };
    }

    public void SetSide(EdgeSide side)
    {
        _side = side;
        Render();
    }

    public void SetExpansionProgress(double progress)
    {
        _expansionProgress = Math.Clamp(progress, 0, 1);
        Render();
    }

    public void TriggerNotificationPulse()
    {
        _notificationStartedAt = _clock.Elapsed.TotalSeconds;
        UpdateTimerInterval();
        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Stop();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        Render();
        if (!_isPlaying && _clock.Elapsed.TotalSeconds - _notificationStartedAt > 1.85)
        {
            UpdateTimerInterval();
        }
    }

    private void UpdateTimerInterval()
    {
        var notificationActive = _clock.Elapsed.TotalSeconds - _notificationStartedAt <= 1.85;
        _timer.Interval = TimeSpan.FromMilliseconds(
            notificationActive
                ? 50
                : _isPlaying
                    ? 83
                    : 200);
    }

    private void Render()
    {
        var width = _host.ActualWidth;
        var height = _host.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var seconds = _clock.Elapsed.TotalSeconds;
        var signal = _motionSource.Sample(seconds, _isPlaying).Normalize();
        var direction = _side == EdgeSide.Right ? -1d : 1d;
        var anchor = _side == EdgeSide.Right ? width - 1.5 : 1.5;
        var notificationAge = seconds - _notificationStartedAt;

        foreach (var layer in _layers)
        {
            layer.Update(
                width,
                height,
                anchor,
                direction,
                seconds,
                signal,
                _intensity,
                _expansionProgress,
                notificationAge);
        }
    }

    private sealed class WaveLayer
    {
        private readonly PathFigure[] _figures;
        private readonly BezierSegment[][] _segments;
        private readonly Point[] _points = new Point[KnotCount];
        private readonly double _baseReach;
        private readonly double _centerReach;
        private readonly double _orbReach;
        private readonly double _expansionReach;
        private readonly double _amplitude;
        private readonly double _phase;

        public WaveLayer(
            IReadOnlyList<XamlPath> paths,
            double baseReach,
            double centerReach,
            double orbReach,
            double expansionReach,
            double amplitude,
            double phase)
        {
            _baseReach = baseReach;
            _centerReach = centerReach;
            _orbReach = orbReach;
            _expansionReach = expansionReach;
            _amplitude = amplitude;
            _phase = phase;
            _figures = new PathFigure[paths.Count];
            _segments = new BezierSegment[paths.Count][];
            for (var pathIndex = 0; pathIndex < paths.Count; pathIndex++)
            {
                var figure = new PathFigure { IsClosed = false, IsFilled = false };
                var geometry = new PathGeometry();
                var segments = new BezierSegment[KnotCount - 1];
                geometry.Figures.Add(figure);

                for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
                {
                    var segment = new BezierSegment();
                    segments[segmentIndex] = segment;
                    figure.Segments.Add(segment);
                }

                _figures[pathIndex] = figure;
                _segments[pathIndex] = segments;
                paths[pathIndex].Data = geometry;
            }
        }

        public WaveLayer(
            XamlPath path,
            double baseReach,
            double centerReach,
            double orbReach,
            double expansionReach,
            double amplitude,
            double phase)
            : this(
                [path],
                baseReach,
                centerReach,
                orbReach,
                expansionReach,
                amplitude,
                phase)
        {
        }

        public void Update(
            double width,
            double height,
            double anchor,
            double direction,
            double seconds,
            EdgeMotionSignal signal,
            double intensity,
            double expansionProgress,
            double notificationAge)
        {
            for (var index = 0; index < KnotCount; index++)
            {
                var normalizedY = index / (double)(KnotCount - 1);
                const double verticalInset = 0.5;
                var centerEnvelope = Gaussian(normalizedY, 0.5, 3.15);
                var bubbleWaist = Gaussian(normalizedY, 0.5, 10.8);
                var ambientEnvelope = 0.16 + (centerEnvelope * 0.84);

                var breathing =
                    Math.Sin((normalizedY * 18.2) + (seconds * (0.48 + signal.MidBand)) + _phase) +
                    (Math.Sin((normalizedY * 31.5) - (seconds * (0.28 + signal.LowBand)) + (_phase * 0.7)) * 0.28) +
                    (Math.Sin((normalizedY * 7.6) + (seconds * 0.18) - _phase) * 0.16);

                var expandedEnvelope = Gaussian(normalizedY, 0.5, 2.25);
                var baseReach = _baseReach +
                                (_centerReach * centerEnvelope) +
                                (_orbReach * bubbleWaist) +
                                (_expansionReach * expansionProgress * expandedEnvelope);
                var motionAmplitude =
                    (0.8 + (signal.Energy * 5.2)) *
                    _amplitude *
                    intensity *
                    ambientEnvelope;
                var notificationDisplacement = GetNotificationDisplacement(normalizedY, notificationAge);
                var displacement =
                    baseReach +
                    (breathing * motionAmplitude) +
                    (notificationDisplacement * _amplitude);
                displacement = Math.Clamp(displacement, 0, width - 3);

                _points[index] = new Point(
                    anchor + (direction * displacement),
                    verticalInset + (normalizedY * (height - (verticalInset * 2))));
            }

            ApplySmoothCurve();
        }

        private void ApplySmoothCurve()
        {
            for (var pathIndex = 0; pathIndex < _figures.Length; pathIndex++)
            {
                _figures[pathIndex].StartPoint = _points[0];
                var segments = _segments[pathIndex];
                for (var index = 0; index < segments.Length; index++)
                {
                    var previous = _points[Math.Max(0, index - 1)];
                    var start = _points[index];
                    var end = _points[index + 1];
                    var next = _points[Math.Min(KnotCount - 1, index + 2)];
                    var segment = segments[index];

                    segment.Point1 = new Point(
                        start.X + ((end.X - previous.X) / 6),
                        start.Y + ((end.Y - previous.Y) / 6));
                    segment.Point2 = new Point(
                        end.X - ((next.X - start.X) / 6),
                        end.Y - ((next.Y - start.Y) / 6));
                    segment.Point3 = end;
                }
            }
        }

        private static double GetNotificationDisplacement(double normalizedY, double age)
        {
            if (age is < 0 or > 1.85)
            {
                return 0;
            }

            var incoming = 0d;
            if (age <= 0.28)
            {
                var travel = Math.Clamp(age / 0.28, 0, 1);
                var pulseY = 0.10 + (travel * 0.40);
                incoming = Gaussian(normalizedY, pulseY, 24) * 9.5;
            }

            var ripple = 0d;
            if (age >= 0.20)
            {
                var rippleTime = Math.Clamp((age - 0.20) / 1.45, 0, 1);
                var radius = rippleTime * 0.48;
                var distance = Math.Abs(normalizedY - 0.5);
                ripple = Math.Exp(-Math.Pow((distance - radius) * 22, 2)) *
                         (1 - rippleTime) * 12;
            }

            return incoming + ripple;
        }

        private static double Gaussian(double value, double center, double sharpness) =>
            Math.Exp(-Math.Pow((value - center) * sharpness, 2));
    }
}
