namespace NyKurEdge.Core.Media;

public interface IMediaSessionService : IAsyncDisposable
{
    MediaSnapshot Current { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<bool> TogglePlayPauseAsync(CancellationToken cancellationToken = default);

    Task<bool> SkipPreviousAsync(CancellationToken cancellationToken = default);

    Task<bool> SkipNextAsync(CancellationToken cancellationToken = default);

    Task<bool> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}
