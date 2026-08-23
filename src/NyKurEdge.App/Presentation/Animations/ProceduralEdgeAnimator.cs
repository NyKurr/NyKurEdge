using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Shapes;
using NyKurEdge.Core.Settings;
using Windows.Foundation;

namespace NyKurEdge.App.Presentation.Animations;

public sealed class ProceduralEdgeAnimator : IDisposable
{
    private readonly Polyline _wave;
    private readonly FrameworkElement _host;
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DateTimeOffset _pulseStarted = DateTimeOffset.MinValue;
    private bool _isPlaying;
    private double _intensity = 1;

    public ProceduralEdgeAnimator(Polyline wave, FrameworkElement host)
    {
        _wave = wave;
        _host = host;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TimeSpan.FromMilliseconds(135);
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _timer.Start();
        Render();
    }

    public void Stop()
    {
        _timer.Stop();
    }

    public void SetPlaying(bool isPlaying)
    {
        _isPlaying = isPlaying;
        _timer.Interval = TimeSpan.FromMilliseconds(isPlaying ? 33 : 135);
    }

    public void SetIntensity(AnimationIntensity intensity)
    {
        _intensity = intensity switch
        {
            AnimationIntensity.Quiet => 0.7,
            AnimationIntensity.Expressive => 1.3,
            _ => 1,
        };
    }

    public void TriggerNotificationPulse()
    {
        _pulseStarted = DateTimeOffset.Now;
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

    private void OnTick(DispatcherQueueTimer sender, object args) => Render();

    private void Render()
    {
        var height = _host.ActualHeight;
        var width = _host.ActualWidth;
        if (height <= 0 || width <= 0)
        {
            return;
        }

        var seconds = _clock.Elapsed.TotalSeconds;
        var pulseAge = (DateTimeOffset.Now - _pulseStarted).TotalSeconds;
        var pulse = pulseAge is >= 0 and < 1.8
            ? Math.Pow(1 - (pulseAge / 1.8), 2) * 3.8
            : 0;
        var baseAmplitude = (_isPlaying ? 2.6 : 0.65) * _intensity;
        var center = width / 2;
        var step = Math.Clamp(height / 46, 14, 28);

        _wave.Points.Clear();
        for (var y = 0d; y <= height + step; y += step)
        {
            var normalized = y / height;
            var centerEnvelope = 0.34 + (0.66 * Math.Exp(-Math.Pow((normalized - 0.5) * 2.4, 2)));
            var movement =
                Math.Sin((normalized * 17) + (seconds * (_isPlaying ? 4.2 : 1.1))) +
                (Math.Sin((normalized * 31) - (seconds * 2.1)) * 0.34);
            var pulseEnvelope = Math.Exp(-Math.Pow((normalized - 0.5) * 6, 2));
            var x = center + (movement * baseAmplitude * centerEnvelope) + (movement * pulse * pulseEnvelope);
            _wave.Points.Add(new Point(x, y));
        }
    }
}
