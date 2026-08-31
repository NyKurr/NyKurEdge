using System.Diagnostics;

namespace NyKurEdge.Core.AudioVisualization;

/// <summary>
/// A normalized, immutable view of the most recently analyzed system-output audio.
/// </summary>
/// <param name="Energy">Overall signal energy in the inclusive range 0..1.</param>
/// <param name="LowBand">Low-frequency energy in the inclusive range 0..1.</param>
/// <param name="MidBand">Mid-frequency energy in the inclusive range 0..1.</param>
/// <param name="HighBand">High-frequency energy in the inclusive range 0..1.</param>
/// <param name="Timestamp">The <see cref="Stopwatch.GetTimestamp"/> value at analysis time.</param>
public readonly record struct AudioSpectrumSnapshot(
    double Energy,
    double LowBand,
    double MidBand,
    double HighBand,
    long Timestamp)
{
    /// <summary>
    /// Returns whether this sample was produced within <paramref name="maximumAge"/>.
    /// A default snapshot and non-positive maximum ages are never considered fresh.
    /// </summary>
    public bool IsFresh(TimeSpan maximumAge)
    {
        if (Timestamp <= 0 || maximumAge <= TimeSpan.Zero)
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        return Timestamp <= now && Stopwatch.GetElapsedTime(Timestamp, now) <= maximumAge;
    }
}
