using Windows.Media.Control;
using Windows.Storage.Streams;
using NyKurEdge.Core.Events;
using NyKurEdge.Core.Media;

namespace NyKurEdge.Infrastructure.Media;

public sealed class WindowsMediaSessionService(IEventBus eventBus) : IMediaSessionService
{
    private const ulong MaximumArtworkBytes = 8 * 1024 * 1024;

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot _current = MediaSnapshot.Empty;
    private bool _started;

    public MediaSnapshot Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        _started = true;
        await AttachCurrentSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }

        DetachSession();
        _manager = null;
        Volatile.Write(ref _current, MediaSnapshot.Empty);
        await eventBus.PublishAsync(new MediaChanged(MediaSnapshot.Empty), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Current.PlaybackState == MediaPlaybackState.Playing
            ? await session.TryPauseAsync()
            : await session.TryPlayAsync();
    }

    public async Task<bool> SkipPreviousAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await session.TrySkipPreviousAsync();
    }

    public async Task<bool> SkipNextAsync(CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await session.TrySkipNextAsync();
    }

    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        var session = _session;
        if (session is null || position < TimeSpan.Zero)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await session.TryChangePlaybackPositionAsync(position.Ticks);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _refreshGate.Dispose();
    }

    private async Task AttachCurrentSessionAsync(CancellationToken cancellationToken)
    {
        var nextSession = _manager?.GetCurrentSession();
        if (ReferenceEquals(_session, nextSession))
        {
            await RefreshAsync(refreshMetadata: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        DetachSession();
        _session = nextSession;
        if (_session is null)
        {
            Volatile.Write(ref _current, MediaSnapshot.Empty);
            await eventBus.PublishAsync(new MediaChanged(MediaSnapshot.Empty), cancellationToken).ConfigureAwait(false);
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        await RefreshAsync(refreshMetadata: true, cancellationToken).ConfigureAwait(false);
    }

    private void DetachSession()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    private async Task RefreshAsync(bool refreshMetadata, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session;
            if (session is null)
            {
                return;
            }

            var previous = Current;
            var title = previous.Title;
            var artist = previous.Artist;
            var album = previous.Album;
            var artwork = previous.Artwork;
            var contentType = previous.ArtworkContentType;

            if (refreshMetadata || !previous.HasSession)
            {
                var media = await session.TryGetMediaPropertiesAsync();
                title = string.IsNullOrWhiteSpace(media.Title) ? "Untitled media" : media.Title.Trim();
                artist = string.IsNullOrWhiteSpace(media.Artist) ? "Unknown artist" : media.Artist.Trim();
                album = media.AlbumTitle?.Trim() ?? string.Empty;
                (artwork, contentType) = await ReadArtworkAsync(media.Thumbnail, cancellationToken).ConfigureAwait(false);
            }

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero;
            var position = duration > TimeSpan.Zero
                ? timeline.Position - timeline.StartTime
                : timeline.Position;
            var state = MapPlaybackState(playback.PlaybackStatus);

            var snapshot = new MediaSnapshot(
                title,
                artist,
                album,
                session.SourceAppUserModelId ?? string.Empty,
                state,
                new MediaTimeline(
                    position < TimeSpan.Zero ? TimeSpan.Zero : position,
                    duration,
                    DateTimeOffset.Now),
                artwork,
                contentType);

            Volatile.Write(ref _current, snapshot);
            await eventBus.PublishAsync(new MediaChanged(snapshot), cancellationToken).ConfigureAwait(false);
            if (previous.PlaybackState != snapshot.PlaybackState)
            {
                await eventBus.PublishAsync(new PlaybackStateChanged(snapshot.PlaybackState), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<(byte[]? Bytes, string? ContentType)> ReadArtworkAsync(
        IRandomAccessStreamReference? artworkReference,
        CancellationToken cancellationToken)
    {
        if (artworkReference is null)
        {
            return (null, null);
        }

        using var stream = await artworkReference.OpenReadAsync();
        if (stream.Size is 0 or > MaximumArtworkBytes)
        {
            return (null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        _ = await reader.LoadAsync((uint)stream.Size);
        cancellationToken.ThrowIfCancellationRequested();
        reader.ReadBytes(bytes);
        return (bytes, stream.ContentType);
    }

    private static MediaPlaybackState MapPlaybackState(
        GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackState.Changing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackState.Unavailable,
            _ => MediaPlaybackState.Unavailable,
        };

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => Observe(AttachCurrentSessionAsync(CancellationToken.None));

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => Observe(AttachCurrentSessionAsync(CancellationToken.None));

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => Observe(RefreshAsync(refreshMetadata: true, CancellationToken.None));

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => Observe(RefreshAsync(refreshMetadata: false, CancellationToken.None));

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => Observe(RefreshAsync(refreshMetadata: false, CancellationToken.None));

    private static async void Observe(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Media session refresh failed: {exception}");
        }
    }
}
