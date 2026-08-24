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
/// Deterministic low-frequency value noise used until real audio analysis is
/// connected. This is explicitly procedural and is not audio-reactive.
/// </summary>
public sealed class ProceduralEdgeMotionSource : IEdgeMotionSource
{
    public EdgeMotionSignal Sample(double elapsedSeconds, bool isPlaying)
    {
        if (!isPlaying)
        {
            return new EdgeMotionSignal(
                0.12 + (Noise(elapsedSeconds * 0.16, 11) * 0.06),
                0.10 + (Noise(elapsedSeconds * 0.11, 29) * 0.07),
                0.11 + (Noise(elapsedSeconds * 0.19, 47) * 0.06),
                0.06 + (Noise(elapsedSeconds * 0.23, 83) * 0.04));
        }

        return new EdgeMotionSignal(
            0.42 + (Noise(elapsedSeconds * 0.78, 17) * 0.25),
            0.34 + (Noise(elapsedSeconds * 0.52, 31) * 0.30),
            0.31 + (Noise(elapsedSeconds * 0.94, 59) * 0.28),
            0.18 + (Noise(elapsedSeconds * 1.31, 97) * 0.24)).Normalize();
    }

    private static double Noise(double value, int seed)
    {
        var left = (int)Math.Floor(value);
        var amount = value - left;
        amount = amount * amount * amount * ((amount * ((amount * 6) - 15)) + 10);
        return Lerp(Hash(left, seed), Hash(left + 1, seed), amount);
    }

    private static double Hash(int value, int seed)
    {
        unchecked
        {
            var hash = (uint)(value * 374761393 + seed * 668265263);
            hash = (hash ^ (hash >> 13)) * 1274126177;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFF) / (double)0x00FFFFFF;
        }
    }

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);
}
