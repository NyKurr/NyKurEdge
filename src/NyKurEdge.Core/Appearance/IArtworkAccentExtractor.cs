namespace NyKurEdge.Core.Appearance;

public interface IArtworkAccentExtractor
{
    Task<AccentColor?> ExtractAsync(
        ReadOnlyMemory<byte> encodedArtwork,
        CancellationToken cancellationToken = default);
}
