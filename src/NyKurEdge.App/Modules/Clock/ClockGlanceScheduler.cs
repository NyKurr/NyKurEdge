using System.Globalization;
using NyKurEdge.Core.Glances;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.App.Modules.Clock;

public sealed class ClockGlanceScheduler(
    SettingsService settings,
    IGlanceCoordinator glanceCoordinator) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _settingsChanged = new(0, 1);
    private Task? _worker;

    public void Start()
    {
        _worker ??= RunAsync(_lifetime.Token);
    }

    public void NotifySettingsChanged()
    {
        if (_settingsChanged.CurrentCount == 0)
        {
            _settingsChanged.Release();
        }
    }

    public Task PreviewAsync(CancellationToken cancellationToken = default) =>
        ShowClockAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _settingsChanged.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var clockSettings = settings.Current.Clock;
            if (!clockSettings.Enabled)
            {
                await _settingsChanged.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            var delay = GetDelayToNextInterval(DateTimeOffset.Now, clockSettings.IntervalMinutes);
            if (await WaitForScheduleAsync(delay, cancellationToken).ConfigureAwait(false))
            {
                await ShowClockAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> WaitForScheduleAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var iteration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(delay, iteration.Token);
        var settingsTask = _settingsChanged.WaitAsync(iteration.Token);
        var completed = await Task.WhenAny(delayTask, settingsTask).ConfigureAwait(false);
        iteration.Cancel();

        try
        {
            await (completed == delayTask ? settingsTask : delayTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return completed == delayTask;
    }

    private Task ShowClockAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var duration = TimeSpan.FromSeconds(settings.Current.Clock.HoldSeconds);
        return glanceCoordinator.ShowAsync(
            new GlancePresentation(
                Guid.NewGuid(),
                GlanceKind.Clock,
                "TIME",
                now.ToString("HH:mm", CultureInfo.CurrentCulture),
                now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture),
                duration),
            cancellationToken);
    }

    internal static TimeSpan GetDelayToNextInterval(DateTimeOffset now, int intervalMinutes)
    {
        intervalMinutes = Math.Clamp(intervalMinutes, 1, 24 * 60);
        var minutesSinceMidnight = (now.Hour * 60) + now.Minute;
        var nextBucket = ((minutesSinceMidnight / intervalMinutes) + 1) * intervalMinutes;
        var next = now.Date.AddMinutes(nextBucket);
        return next - now;
    }
}
