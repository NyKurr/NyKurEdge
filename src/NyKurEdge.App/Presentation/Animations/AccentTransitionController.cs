using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using NyKurEdge.Core.Appearance;
using Windows.UI;

namespace NyKurEdge.App.Presentation.Animations;

public sealed class AccentTransitionController : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private readonly (SolidColorBrush Brush, byte Alpha)[] _brushes;
    private readonly Stopwatch _clock = new();
    private AccentColor _current = AccentColor.Default;
    private OklabColor _from;
    private OklabColor _to;

    public AccentTransitionController(params (SolidColorBrush Brush, byte Alpha)[] brushes)
    {
        _brushes = brushes;
        _from = Oklab.FromSrgb(_current.Red, _current.Green, _current.Blue);
        _to = _from;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTick;
        Apply(_current);
    }

    public void TransitionTo(AccentColor accent)
    {
        _timer.Stop();
        _from = Oklab.FromSrgb(_current.Red, _current.Green, _current.Blue);
        _to = Oklab.FromSrgb(accent.Red, accent.Green, accent.Blue);
        _clock.Restart();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Stop();
    }

    private void OnTick(DispatcherQueueTimer sender, object args)
    {
        var progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / 420d, 0, 1);
        var eased = progress * progress * (3 - (2 * progress));
        var color = Oklab.ToSrgb(Oklab.Lerp(_from, _to, eased));
        Apply(color);

        if (progress >= 1)
        {
            _timer.Stop();
            _clock.Stop();
        }
    }

    private void Apply(AccentColor accent)
    {
        _current = accent;
        foreach (var (brush, alpha) in _brushes)
        {
            brush.Color = Color.FromArgb(alpha, accent.Red, accent.Green, accent.Blue);
        }
    }
}
