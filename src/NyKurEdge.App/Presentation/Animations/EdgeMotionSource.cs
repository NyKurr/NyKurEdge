namespace NyKurEdge.App.Presentation.Animations;

public readonly record struct EdgeMotionSignal(
    double Energy,
    double LowBand,
    double MidBand,
    double HighBand)
{
    public EdgeMotionSignal Normalize() => new(
        Math.Clamp(Energy, 0, 1),
        Math.Clamp(LowBand, 0, 1),
        Math.Clamp(MidBand, 0, 1),
        Math.Clamp(HighBand, 0, 1));
}

/// <summary>
/// Supplies normalized motion energy to the Edge renderer. A future loopback-audio
/// adapter can implement this contract without changing the visual surface.
/// </summary>
public interface IEdgeMotionSource
{
    EdgeMotionSignal Sample(double elapsedSeconds, bool isPlaying);
}

/// <summary>
/// Tasteful deterministic motion used until real audio analysis is connected.
/// This source is intentionally procedural and is not audio-reactive.
/// </summary>
public sealed class ProceduralEdgeMotionSource : IEdgeMotionSource
{
    public EdgeMotionSignal Sample(double elapsedSeconds, bool isPlaying)
    {
        if (!isPlaying)
        {
            var breath = 0.5 + (Math.Sin(elapsedSeconds * 0.39) * 0.5);
            var slowDrift = 0.5 + (Math.Sin((elapsedSeconds * 0.17) + 1.7) * 0.5);
            return new EdgeMotionSignal(
                0.12 + (breath * 0.07) + (slowDrift * 0.025),
                0.11 + (slowDrift * 0.055),
                0.13 + (breath * 0.06),
                0.08 + ((1 - breath) * 0.025));
        }

        // This deliberately suggests rhythm without pretending to be sampled audio.
        // The small incommensurate oscillators avoid an obvious repeating equalizer loop.
        var pulse = 0.5 + (Math.Sin(
            (elapsedSeconds * 1.72) +
            (Math.Sin(elapsedSeconds * 0.31) * 0.24)) * 0.5);
        var swell = 0.5 + (Math.Sin((elapsedSeconds * 0.73) + 0.8) * 0.5);
        var shimmer = 0.5 + (Math.Sin((elapsedSeconds * 2.41) + 2.1) * 0.5);

        return new EdgeMotionSignal(
            0.42 + (pulse * 0.18) + (swell * 0.09),
            0.38 + (swell * 0.20) + (pulse * 0.08),
            0.34 + (pulse * 0.17) + (shimmer * 0.10),
            0.22 + (shimmer * 0.15)).Normalize();
    }
}
