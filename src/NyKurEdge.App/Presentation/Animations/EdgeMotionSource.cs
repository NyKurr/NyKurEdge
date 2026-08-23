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
            return new EdgeMotionSignal(
                0.18 + (Math.Sin(elapsedSeconds * 0.72) * 0.035),
                0.16 + (Math.Sin(elapsedSeconds * 0.43) * 0.025),
                0.20 + (Math.Sin(elapsedSeconds * 0.61 + 1.2) * 0.03),
                0.12);
        }

        return new EdgeMotionSignal(
            0.62 + (Math.Sin(elapsedSeconds * 1.8) * 0.09),
            0.58 + (Math.Sin(elapsedSeconds * 1.15 + 0.4) * 0.11),
            0.52 + (Math.Sin(elapsedSeconds * 2.05 + 1.1) * 0.10),
            0.34 + (Math.Sin(elapsedSeconds * 2.7 + 2.0) * 0.07)).Normalize();
    }
}
