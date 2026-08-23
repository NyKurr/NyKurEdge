using NyKurEdge.App.Modules.Clock;
using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.Display;
using NyKurEdge.Core.Events;
using NyKurEdge.Core.Glances;
using NyKurEdge.Core.Media;
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;
using NyKurEdge.Infrastructure.Appearance;
using NyKurEdge.Infrastructure.Display;
using NyKurEdge.Infrastructure.Media;
using NyKurEdge.Infrastructure.Notifications;
using NyKurEdge.Infrastructure.Settings;

namespace NyKurEdge.App;

public sealed class AppServices : IAsyncDisposable
{
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private readonly IDisposable _settingsSubscription;
    private bool _runtimeStarted;

    public AppServices()
    {
        EventBus = new EventBus();
        Settings = new SettingsService(new JsonSettingsStore(), EventBus);
        DisplayService = new WindowsDisplayService();
        Media = new WindowsMediaSessionService(EventBus);
        Notifications = new WindowsNotificationListenerService(EventBus);
        Startup = new WindowsStartupService();
        AccentExtractor = new WindowsArtworkAccentExtractor();
        Glances = new GlanceCoordinator(EventBus);
        Clock = new ClockGlanceScheduler(Settings, Glances);

        _settingsSubscription = EventBus.Subscribe<SettingsChanged>(OnSettingsChangedAsync);
    }

    public IEventBus EventBus { get; }

    public SettingsService Settings { get; }

    public IDisplayService DisplayService { get; }

    public IMediaSessionService Media { get; }

    public INotificationListenerService Notifications { get; }

    public IStartupService Startup { get; }

    public IArtworkAccentExtractor AccentExtractor { get; }

    public IGlanceCoordinator Glances { get; }

    public ClockGlanceScheduler Clock { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Settings.InitializeAsync(cancellationToken);

    public async Task StartRuntimeAsync(CancellationToken cancellationToken = default)
    {
        _runtimeStarted = true;
        Clock.Start();
        await ApplyModuleSettingsAsync(Settings.Current, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _runtimeStarted = false;
        _settingsSubscription.Dispose();
        await Clock.DisposeAsync().ConfigureAwait(false);
        await Notifications.DisposeAsync().ConfigureAwait(false);
        await Media.DisposeAsync().ConfigureAwait(false);
        Glances.Dispose();
        Settings.Dispose();
        _moduleGate.Dispose();
    }

    private async ValueTask OnSettingsChangedAsync(
        SettingsChanged message,
        CancellationToken cancellationToken)
    {
        Clock.NotifySettingsChanged();
        if (_runtimeStarted)
        {
            await ApplyModuleSettingsAsync(message.Settings, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplyModuleSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (settings.Media.Enabled)
            {
                await Media.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
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
        finally
        {
            _moduleGate.Release();
        }
    }
}
