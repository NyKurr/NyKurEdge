namespace NyKurEdge.Core.Media;

public enum MediaPlaybackState
{
    Unavailable,
    Stopped,
    Paused,
    Playing,
    Changing,
}

public sealed record MediaTimeline(
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset UpdatedAt)
{
    public static MediaTimeline Empty { get; } = new(TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);

    public double Progress => Duration > TimeSpan.Zero
        ? Math.Clamp(Position.TotalMilliseconds / Duration.TotalMilliseconds, 0, 1)
        : 0;
}

public sealed record MediaSnapshot(
    string Title,
    string Artist,
    string Album,
    string SourceAppId,
    MediaPlaybackState PlaybackState,
    MediaTimeline Timeline,
    byte[]? Artwork,
    string? ArtworkContentType)
{
    public static MediaSnapshot Empty { get; } = new(
        "Nothing playing",
        "Start media in any compatible Windows app",
        string.Empty,
        string.Empty,
        MediaPlaybackState.Unavailable,
        MediaTimeline.Empty,
        null,
        null);

    public bool HasSession => PlaybackState != MediaPlaybackState.Unavailable;
}
