using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NyKurEdge.Core.Appearance;
using NyKurEdge.Core.Events;
using NyKurEdge.Core.Glances;
using NyKurEdge.Core.Media;
using NyKurEdge.Core.Notifications;
using NyKurEdge.Core.Settings;
using Windows.Storage.Streams;

namespace NyKurEdge.App.Presentation.ViewModels;

public sealed class EdgeViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherQueueTimer _progressTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private CancellationTokenSource? _artworkCancellation;
    private byte[]? _lastArtwork;
    private uint? _latestNotificationId;
    private MediaSnapshot _media = MediaSnapshot.Empty;
    private AppSettings _settings;
    private string _mediaTitle = MediaSnapshot.Empty.Title;
    private string _mediaArtist = MediaSnapshot.Empty.Artist;
    private string _mediaSource = "WINDOWS MEDIA";
    private string _playPauseGlyph = "\uE768";
    private string _positionText = "0:00";
    private string _durationText = "0:00";
    private double _mediaProgress;
    private bool _isMediaAvailable;
    private bool _isPlaying;
    private ImageSource? _artwork;
    private string _headerTime = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
    private bool _hasNotification;
    private string _notificationApplication = "Notifications";
    private string _notificationTitle = "No unread notifications";
    private string _notificationMessage = "Permission remains off until you enable it.";
    private string _notificationTime = string.Empty;
    private ImageSource? _notificationIcon;
    private NotificationAccessState _notificationAccess;
    private bool _isGlanceVisible;
    private string _glanceEyebrow = "TIME";
    private string _glancePrimary = string.Empty;
    private string _glanceSecondary = string.Empty;
    private bool _disposed;

    public EdgeViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _settings = services.Settings.Current;
        _notificationAccess = services.Notifications.AccessState;

        _subscriptions.Add(services.EventBus.Subscribe<MediaChanged>(OnMediaChangedAsync));
        _subscriptions.Add(services.EventBus.Subscribe<NotificationReceived>(OnNotificationReceivedAsync));
        _subscriptions.Add(services.EventBus.Subscribe<NotificationDismissed>(OnNotificationDismissedAsync));
        _subscriptions.Add(services.EventBus.Subscribe<SettingsChanged>(OnSettingsChangedAsync));
        _subscriptions.Add(services.EventBus.Subscribe<GlanceRequested>(OnGlanceRequestedAsync));
        _subscriptions.Add(services.EventBus.Subscribe<GlanceEnded>(OnGlanceEndedAsync));

        _progressTimer = _dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += OnProgressTimerTick;

        _clockTimer = _dispatcher.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(20);
        _clockTimer.IsRepeating = true;
        _clockTimer.Tick += OnClockTimerTick;
        _clockTimer.Start();

        ApplyMedia(services.Media.Current);
        UpdateEmptyNotificationCopy();
    }

    public event Action<AccentColor>? AccentRequested;

    public event Action? NotificationArrived;

    public event Action<bool>? GlanceVisibilityChanged;

    public string MediaTitle { get => _mediaTitle; private set => SetProperty(ref _mediaTitle, value); }

    public string MediaArtist { get => _mediaArtist; private set => SetProperty(ref _mediaArtist, value); }

    public string MediaSource { get => _mediaSource; private set => SetProperty(ref _mediaSource, value); }

    public string PlayPauseGlyph { get => _playPauseGlyph; private set => SetProperty(ref _playPauseGlyph, value); }

    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }

    public double MediaProgress { get => _mediaProgress; set => SetProperty(ref _mediaProgress, value); }

    public bool IsMediaAvailable { get => _isMediaAvailable; private set => SetProperty(ref _isMediaAvailable, value); }

    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }

    public ImageSource? Artwork { get => _artwork; private set => SetProperty(ref _artwork, value); }

    public string HeaderTime { get => _headerTime; private set => SetProperty(ref _headerTime, value); }

    public bool HasNotification { get => _hasNotification; private set => SetProperty(ref _hasNotification, value); }

    public string NotificationApplication
    {
        get => _notificationApplication;
        private set => SetProperty(ref _notificationApplication, value);
    }

    public string NotificationTitle { get => _notificationTitle; private set => SetProperty(ref _notificationTitle, value); }

    public string NotificationMessage
    {
        get => _notificationMessage;
        private set => SetProperty(ref _notificationMessage, value);
    }

    public string NotificationTime { get => _notificationTime; private set => SetProperty(ref _notificationTime, value); }

    public ImageSource? NotificationIcon
    {
        get => _notificationIcon;
        private set => SetProperty(ref _notificationIcon, value);
    }

    public NotificationAccessState NotificationAccess
    {
        get => _notificationAccess;
        private set
        {
            if (SetProperty(ref _notificationAccess, value))
            {
                RaisePropertyChanged(nameof(NotificationAccessLabel));
            }
        }
    }

    public string NotificationAccessLabel => NotificationAccess switch
    {
        NotificationAccessState.Allowed => "Access allowed",
        NotificationAccessState.Denied => "Access denied in Windows Settings",
        NotificationAccessState.Unspecified => "Access not requested",
        _ => "Notification listener unavailable",
    };

    public bool IsGlanceVisible { get => _isGlanceVisible; private set => SetProperty(ref _isGlanceVisible, value); }

    public string GlanceEyebrow { get => _glanceEyebrow; private set => SetProperty(ref _glanceEyebrow, value); }

    public string GlancePrimary { get => _glancePrimary; private set => SetProperty(ref _glancePrimary, value); }

    public string GlanceSecondary { get => _glanceSecondary; private set => SetProperty(ref _glanceSecondary, value); }

    public AppSettings Settings => _settings;

    public Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default) =>
        _services.Media.TogglePlayPauseAsync(cancellationToken);

    public Task<bool> SkipPreviousAsync(CancellationToken cancellationToken = default) =>
        _services.Media.SkipPreviousAsync(cancellationToken);

    public Task<bool> SkipNextAsync(CancellationToken cancellationToken = default) =>
        _services.Media.SkipNextAsync(cancellationToken);

    public Task<bool> SeekAsync(double progress, CancellationToken cancellationToken = default)
    {
        progress = Math.Clamp(progress, 0, 1);
        return _services.Media.SeekAsync(
            TimeSpan.FromTicks((long)(_media.Timeline.Duration.Ticks * progress)),
            cancellationToken);
    }

    public async Task<NotificationAccessState> RequestNotificationAccessAsync(
        CancellationToken cancellationToken = default)
    {
        NotificationAccess = await _services.Notifications.RequestAccessAsync(cancellationToken);
        if (NotificationAccess == NotificationAccessState.Allowed)
        {
            await _services.Settings.UpdateAsync(
                settings => settings with
                {
                    Notifications = settings.Notifications with { Enabled = true },
                },
                cancellationToken);
            await _services.Notifications.StartAsync(cancellationToken);
        }

        return NotificationAccess;
    }

    public void RefreshNotificationAccess()
    {
        NotificationAccess = _services.Notifications.AccessState;
        UpdateEmptyNotificationCopy();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _progressTimer.Stop();
        _progressTimer.Tick -= OnProgressTimerTick;
        _clockTimer.Stop();
        _clockTimer.Tick -= OnClockTimerTick;
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    private ValueTask OnMediaChangedAsync(MediaChanged message, CancellationToken cancellationToken)
    {
        _dispatcher.TryEnqueue(() => ApplyMedia(message.Media));
        return ValueTask.CompletedTask;
    }

    private void ApplyMedia(MediaSnapshot media)
    {
        _media = media;
        MediaTitle = media.Title;
        MediaArtist = media.Artist;
        MediaSource = MediaSourceNameFormatter.Format(media.SourceAppId);
        IsMediaAvailable = media.HasSession;
        IsPlaying = media.PlaybackState == MediaPlaybackState.Playing;
        PlayPauseGlyph = IsPlaying ? "\uE769" : "\uE768";
        UpdateProgress();

        if (IsPlaying)
        {
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }

        if (!ReferenceEquals(_lastArtwork, media.Artwork))
        {
            _lastArtwork = media.Artwork;
            _artworkCancellation?.Cancel();
            _artworkCancellation?.Dispose();
            _artworkCancellation = new CancellationTokenSource();
            _ = UpdateArtworkAndAccentAsync(media.Artwork, _artworkCancellation.Token);
        }
        else if (_settings.Appearance.AccentMode == AccentMode.Manual &&
                 AccentColor.TryParse(_settings.Appearance.ManualAccent, out var manualAccent))
        {
            AccentRequested?.Invoke(manualAccent);
        }
    }

    private async Task UpdateArtworkAndAccentAsync(byte[]? artwork, CancellationToken cancellationToken)
    {
        try
        {
            var image = await CreateImageAsync(artwork, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Artwork = image;

            AccentColor accent;
            if (_settings.Appearance.AccentMode == AccentMode.Manual &&
                AccentColor.TryParse(_settings.Appearance.ManualAccent, out var manual))
            {
                accent = manual;
            }
            else if (artwork is not null)
            {
                accent = await _services.AccentExtractor.ExtractAsync(artwork, cancellationToken)
                         ?? AccentColor.Default;
            }
            else
            {
                accent = AccentColor.Default;
            }

            cancellationToken.ThrowIfCancellationRequested();
            AccentRequested?.Invoke(accent);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Artwork presentation failed: {exception}");
        }
    }

    private ValueTask OnNotificationReceivedAsync(
        NotificationReceived message,
        CancellationToken cancellationToken)
    {
        if (!NotificationSourcePolicy.Allows(
                _settings.Notifications,
                message.Notification.SourceAppId))
        {
            return ValueTask.CompletedTask;
        }

        _dispatcher.TryEnqueue(() => _ = ApplyNotificationAsync(message.Notification));
        return ValueTask.CompletedTask;
    }

    private async Task ApplyNotificationAsync(NotificationSnapshot notification)
    {
        _latestNotificationId = notification.Id;
        HasNotification = true;
        NotificationApplication = notification.ApplicationName;
        NotificationTitle = _settings.Notifications.Privacy == NotificationPrivacy.AppOnly
            ? "New notification"
            : notification.Title;
        NotificationMessage = notification.PreviewFor(_settings.Notifications.Privacy);
        NotificationTime = FormatRelativeTime(notification.Timestamp);
        NotificationIcon = await CreateImageAsync(notification.ApplicationIcon, CancellationToken.None);
        NotificationArrived?.Invoke();
    }

    private ValueTask OnNotificationDismissedAsync(
        NotificationDismissed message,
        CancellationToken cancellationToken)
    {
        if (_latestNotificationId == message.NotificationId)
        {
            _dispatcher.TryEnqueue(() =>
            {
                HasNotification = false;
                UpdateEmptyNotificationCopy();
            });
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask OnSettingsChangedAsync(SettingsChanged message, CancellationToken cancellationToken)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _settings = message.Settings;
            RaisePropertyChanged(nameof(Settings));
            UpdateEmptyNotificationCopy();
            if (_settings.Appearance.AccentMode == AccentMode.Manual &&
                AccentColor.TryParse(_settings.Appearance.ManualAccent, out var accent))
            {
                AccentRequested?.Invoke(accent);
            }
            else if (_lastArtwork is not null)
            {
                _ = UpdateArtworkAndAccentAsync(_lastArtwork, CancellationToken.None);
            }
        });
        return ValueTask.CompletedTask;
    }

    private ValueTask OnGlanceRequestedAsync(GlanceRequested message, CancellationToken cancellationToken)
    {
        _dispatcher.TryEnqueue(() =>
        {
            GlanceEyebrow = message.Glance.Eyebrow;
            GlancePrimary = message.Glance.PrimaryText;
            GlanceSecondary = message.Glance.SecondaryText;
            IsGlanceVisible = true;
            GlanceVisibilityChanged?.Invoke(true);
        });
        return ValueTask.CompletedTask;
    }

    private ValueTask OnGlanceEndedAsync(GlanceEnded message, CancellationToken cancellationToken)
    {
        _dispatcher.TryEnqueue(() =>
        {
            IsGlanceVisible = false;
            GlanceVisibilityChanged?.Invoke(false);
        });
        return ValueTask.CompletedTask;
    }

    private void OnProgressTimerTick(DispatcherQueueTimer sender, object args) => UpdateProgress();

    private void UpdateProgress()
    {
        var timeline = _media.Timeline;
        var position = timeline.Position;
        if (IsPlaying && timeline.UpdatedAt != DateTimeOffset.MinValue)
        {
            position += DateTimeOffset.Now - timeline.UpdatedAt;
        }

        if (timeline.Duration > TimeSpan.Zero && position > timeline.Duration)
        {
            position = timeline.Duration;
        }

        MediaProgress = timeline.Duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalMilliseconds / timeline.Duration.TotalMilliseconds, 0, 1)
            : 0;
        PositionText = FormatDuration(position);
        DurationText = FormatDuration(timeline.Duration);
    }

    private void OnClockTimerTick(DispatcherQueueTimer sender, object args)
    {
        HeaderTime = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
    }

    private static async Task<ImageSource?> CreateImageAsync(
        byte[]? bytes,
        CancellationToken cancellationToken)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            _ = await writer.StoreAsync();
            writer.DetachStream();
        }

        cancellationToken.ThrowIfCancellationRequested();
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap;
    }

    private void UpdateEmptyNotificationCopy()
    {
        if (HasNotification)
        {
            return;
        }

        NotificationApplication = "Notifications";
        NotificationTime = string.Empty;
        NotificationIcon = null;

        if (!_settings.Notifications.Enabled)
        {
            NotificationTitle = "Notifications are off";
            NotificationMessage = NotificationAccess == NotificationAccessState.Allowed
                ? "Access is ready. Enable integration in Settings when you want it."
                : "Enable integration in Settings when you're ready.";
            return;
        }

        (NotificationTitle, NotificationMessage) = NotificationAccess switch
        {
            NotificationAccessState.Allowed =>
                ("No unread notifications", "New previews will appear here."),
            NotificationAccessState.Denied =>
                ("Notification access denied", "Allow access in Windows Settings to receive previews."),
            NotificationAccessState.Unavailable =>
                ("Notifications unavailable", "This Windows installation does not expose notification access."),
            _ =>
                ("Notification access required", "Request access from Settings to receive previews."),
        };
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string FormatRelativeTime(DateTimeOffset timestamp)
    {
        var age = DateTimeOffset.Now - timestamp;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m";
        }

        return timestamp.ToString("HH:mm", CultureInfo.CurrentCulture);
    }
}
