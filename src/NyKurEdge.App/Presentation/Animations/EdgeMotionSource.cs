using NyKurEdge.Core.AudioVisualization;

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

/// <summary>
/// Blends the calm procedural tide with live, memory-only WASAPI spectrum data.
/// Audio is the dominant signal while fresh packets are available; the source
/// eases back to the idle tide during capture failure, stale input, or pause.
/// </summary>
public sealed class AudioReactiveEdgeMotionSource(
    IAudioVisualizationService audioVisualization,
    IEdgeMotionSource? idleSource = null) : IEdgeMotionSource
{
    private static readonly TimeSpan FreshAudioWindow = TimeSpan.FromMilliseconds(320);
    private const double LiveAttackSeconds = 0.040;
    private const double LiveReleaseSeconds = 0.155;
    private const double IdleReturnSeconds = 0.62;
    private readonly IEdgeMotionSource _idleSource = idleSource ?? new ProceduralEdgeMotionSource();
    private EdgeMotionSignal _presented;
    private double _lastSampleSeconds;
    private bool _hasPresentedSignal;

    public EdgeMotionSignal Sample(double elapsedSeconds, bool isPlaying)
    {
        var idle = _idleSource.Sample(elapsedSeconds, isPlaying: false).Normalize();
        var spectrum = audioVisualization.Current;
        var hasLiveAudio = isPlaying && spectrum.IsFresh(FreshAudioWindow);
        var target = hasLiveAudio ? CreateAudioTarget(idle, spectrum) : idle;

        if (!_hasPresentedSignal)
        {
            _presented = target;
            _lastSampleSeconds = elapsedSeconds;
            _hasPresentedSignal = true;
            return _presented;
        }

        var deltaSeconds = Math.Clamp(elapsedSeconds - _lastSampleSeconds, 0, 0.12);
        _lastSampleSeconds = elapsedSeconds;
        _presented = new EdgeMotionSignal(
            Follow(_presented.Energy, target.Energy, deltaSeconds, hasLiveAudio),
            Follow(_presented.LowBand, target.LowBand, deltaSeconds, hasLiveAudio),
            Follow(_presented.MidBand, target.MidBand, deltaSeconds, hasLiveAudio),
            Follow(_presented.HighBand, target.HighBand, deltaSeconds, hasLiveAudio)).Normalize();
        return _presented;
    }

    private static EdgeMotionSignal CreateAudioTarget(
        EdgeMotionSignal idle,
        AudioSpectrumSnapshot spectrum)
    {
        // The analyzer publishes a deliberately conservative normalized level. A
        // soft noise gate removes the output-device floor, then the concave curve
        // makes quiet musical detail visible without pinning ordinary tracks at 1.
        var energy = ShapeAudio(spectrum.Energy, noiseFloor: 0.020, fullScale: 0.78, exponent: 0.58);
        var low = ShapeAudio(spectrum.LowBand, noiseFloor: 0.014, fullScale: 0.72, exponent: 0.56);
        var mid = ShapeAudio(spectrum.MidBand, noiseFloor: 0.016, fullScale: 0.70, exponent: 0.58);
        var high = ShapeAudio(spectrum.HighBand, noiseFloor: 0.020, fullScale: 0.66, exponent: 0.62);

        return new EdgeMotionSignal(
            idle.Energy + (energy * 0.84),
            idle.LowBand + (low * 0.86),
            idle.MidBand + (mid * 0.88),
            idle.HighBand + (high * 0.82)).Normalize();
    }

    private static double ShapeAudio(double value, double noiseFloor, double fullScale, double exponent)
    {
        var normalized = Math.Clamp((value - noiseFloor) / (fullScale - noiseFloor), 0, 1);

        // A short smooth knee prevents chatter as a quiet endpoint hovers around
        // the gate. Above the knee, musical dynamics retain their full range.
        var knee = Math.Clamp(normalized / 0.075, 0, 1);
        knee = knee * knee * (3 - (2 * knee));
        return Math.Pow(normalized * knee, exponent) * 0.94;
    }

    private static double Follow(double current, double target, double deltaSeconds, bool hasLiveAudio)
    {
        var responseSeconds = !hasLiveAudio
            ? IdleReturnSeconds
            : target > current
                ? LiveAttackSeconds
                : LiveReleaseSeconds;
        var amount = 1 - Math.Exp(-deltaSeconds / responseSeconds);
        return Lerp(current, target, amount);
    }

    private static double Lerp(double from, double to, double amount) =>
        from + ((to - from) * amount);
}
