using NyKurEdge.App.Modules.Clock;
using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.AudioVisualization;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Events;
using NyKurEdge.Core.Glances;
using NyKurEdge.Core.Media;
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;
using NyKurEdge.Infrastructure.Appearance;
using NyKurEdge.Infrastructure.AudioVisualization;
using NyKurEdge.Infrastructure.Display;
using NyKurEdge.Infrastructure.Media;
using NyKurEdge.Infrastructure.Notifications;
using NyKurEdge.Infrastructure.Settings;

namespace NyKurEdge.App;

public sealed class AppServices : IAsyncDisposable
{
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private readonly SemaphoreSlim _audioVisualizationGate = new(1, 1);
    private readonly object _disposeSync = new();
    private readonly IDisposable _settingsSubscription;
    private readonly IDisposable _playbackSubscription;
    private Task? _disposeTask;
    private int _runtimeStarted;
    private int _mediaModuleEnabled;
    private int _disposeState;

    public AppServices()
    {
        EventBus = new EventBus();
        Settings = new SettingsService(new JsonSettingsStore(), EventBus);
        DisplayService = new WindowsDisplayService();
        Media = new WindowsMediaSessionService(EventBus);
        Notifications = new WindowsNotificationListenerService(EventBus);
        Startup = new WindowsStartupService();
        AccentExtractor = new WindowsArtworkAccentExtractor();
        AudioVisualization = new WindowsLoopbackAudioAnalyzer();
        Glances = new GlanceCoordinator(EventBus);
        Clock = new ClockGlanceScheduler(Settings, Glances);

        _settingsSubscription = EventBus.Subscribe<SettingsChanged>(OnSettingsChangedAsync);
        _playbackSubscription = EventBus.Subscribe<PlaybackStateChanged>(OnPlaybackStateChangedAsync);
    }

    public IEventBus EventBus { get; }

    public SettingsService Settings { get; }

    public IDisplayService DisplayService { get; }

    public IMediaSessionService Media { get; }

    public INotificationListenerService Notifications { get; }

    public IStartupService Startup { get; }

    public IArtworkAccentExtractor AccentExtractor { get; }

    public IAudioVisualizationService AudioVisualization { get; }

    public IGlanceCoordinator Glances { get; }

    public ClockGlanceScheduler Clock { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Settings.InitializeAsync(cancellationToken);

    public async Task StartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            Volatile.Write(ref _runtimeStarted, 1);
            Clock.Start();
            await ApplyModuleSettingsCoreAsync(Settings.Current, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposeState, 1);
        Volatile.Write(ref _runtimeStarted, 0);
        Volatile.Write(ref _mediaModuleEnabled, 0);
        _settingsSubscription.Dispose();
        _playbackSubscription.Dispose();

        await _moduleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Clock.DisposeAsync().ConfigureAwait(false);
            await Notifications.DisposeAsync().ConfigureAwait(false);

            await _audioVisualizationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await AudioVisualization.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _audioVisualizationGate.Release();
            }

            await Media.DisposeAsync().ConfigureAwait(false);
            Glances.Dispose();
            Settings.Dispose();
        }
        finally
        {
            Volatile.Write(ref _disposeState, 2);
            _moduleGate.Release();
        }

        // The gates intentionally remain usable until this object is collected. EventBus may
        // already have snapshotted a subscription when it is removed; a late callback can then
        // enter a gate, observe the disposed state, and leave without touching disposed services.
    }

    private async ValueTask OnSettingsChangedAsync(
        SettingsChanged message,
        CancellationToken cancellationToken)
    {
        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return;
            }

            Clock.NotifySettingsChanged();
            if (IsRuntimeRunning)
            {
                await ApplyModuleSettingsCoreAsync(message.Settings, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    private async ValueTask OnPlaybackStateChangedAsync(
        PlaybackStateChanged _,
        CancellationToken cancellationToken)
    {
        if (!IsRuntimeRunning)
        {
            return;
        }

        // The event requests reconciliation; Media.Current is authoritative because an
        // older queued event can arrive after a newer settings or playback transition.
        await ReconcileAudioVisualizationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyModuleSettingsCoreAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _mediaModuleEnabled, settings.Media.Enabled ? 1 : 0);

        if (settings.Media.Enabled)
        {
            await Media.StartAsync(cancellationToken).ConfigureAwait(false);
            await ReconcileAudioVisualizationAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReconcileAudioVisualizationAsync(cancellationToken).ConfigureAwait(false);
            await Media.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.Notifications.Enabled &&
            Notifications.AccessState == NotificationAccessState.Allowed)
        {
            await Notifications.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Notifications.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileAudioVisualizationAsync(CancellationToken cancellationToken)
    {
        if (!IsRuntimeRunning)
        {
            return;
        }

        await _audioVisualizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // EventBus dispatch is synchronous. Keeping this gate separate from _moduleGate
            // lets Media.StartAsync publish PlaybackStateChanged without re-entering the gate
            // already held by settings application.
            if (!IsRuntimeRunning)
            {
                return;
            }

            var shouldCapture = Volatile.Read(ref _mediaModuleEnabled) != 0 &&
                Media.Current.PlaybackState == MediaPlaybackState.Playing;
            if (shouldCapture)
            {
                await AudioVisualization.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AudioVisualization.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _audioVisualizationGate.Release();
        }
    }

    private bool IsRuntimeRunning =>
        Volatile.Read(ref _runtimeStarted) != 0 && Volatile.Read(ref _disposeState) == 0;

    private void ThrowIfDisposing() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}
