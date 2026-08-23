using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using NyKurEdge.Core.Appearance;

namespace NyKurEdge.Infrastructure.Appearance;

public sealed class WindowsArtworkAccentExtractor : IArtworkAccentExtractor
{
    private const uint MaximumSampleDimension = 64;

    public async Task<AccentColor?> ExtractAsync(
        ReadOnlyMemory<byte> encodedArtwork,
        CancellationToken cancellationToken = default)
    {
        if (encodedArtwork.IsEmpty)
        {
            return null;
        }

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(encodedArtwork.ToArray());
            _ = await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        cancellationToken.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1d, MaximumSampleDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        cancellationToken.ThrowIfCancellationRequested();

        var bytes = pixels.DetachPixelData();
        var samples = new Rgba32[bytes.Length / 4];
        for (var source = 0; source + 3 < bytes.Length; source += 4)
        {
            samples[source / 4] = new Rgba32(
                bytes[source],
                bytes[source + 1],
                bytes[source + 2],
                bytes[source + 3]);
        }

        return AccentColorSelector.Select(samples);
    }
}
