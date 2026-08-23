namespace NyKurEdge.Core.Appearance;

public static class AccentColorSelector
{
    public static AccentColor Select(ReadOnlySpan<Rgba32> pixels, AccentColor? fallback = null)
    {
        if (pixels.IsEmpty)
        {
            return fallback ?? AccentColor.Default;
        }

        var buckets = new Dictionary<int, Bucket>();
        var stride = Math.Max(1, pixels.Length / 4096);

        for (var index = 0; index < pixels.Length; index += stride)
        {
            var pixel = pixels[index];
            if (pixel.Alpha < 96)
            {
                continue;
            }

            var lab = Oklab.FromSrgb(pixel.Red, pixel.Green, pixel.Blue);
            var chroma = lab.Chroma;
            if (lab.Lightness is < 0.07 or > 0.94 || chroma < 0.018)
            {
                continue;
            }

            var hue = (lab.HueRadians + Math.PI) / (Math.PI * 2);
            var hueBin = Math.Clamp((int)(hue * 24), 0, 23);
            var lightnessBin = Math.Clamp((int)(lab.Lightness * 6), 0, 5);
            var chromaBin = Math.Clamp((int)(chroma * 22), 0, 7);
            var key = hueBin | (lightnessBin << 5) | (chromaBin << 8);
            var weight = (pixel.Alpha / 255d) * (0.65 + (Math.Min(chroma / 0.16, 1) * 0.35));

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                buckets.Add(key, bucket);
            }

            bucket.Add(lab, weight);
        }

        if (buckets.Count == 0)
        {
            return fallback ?? AccentColor.Default;
        }

        var totalWeight = buckets.Values.Sum(bucket => bucket.Weight);
        var selected = buckets.Values
            .Select(bucket => new Candidate(bucket.Average, Score(bucket, totalWeight)))
            .OrderByDescending(candidate => candidate.Score)
            .First()
            .Color;

        var selectedChroma = selected.Chroma;
        var normalizedLightness = Math.Clamp((selected.Lightness * 0.58) + (0.65 * 0.42), 0.56, 0.72);
        var normalizedChroma = Math.Clamp((selectedChroma * 0.72) + 0.03, 0.055, 0.15);
        var hueRadians = selected.HueRadians;

        return Oklab.ToSrgb(new OklabColor(
            normalizedLightness,
            Math.Cos(hueRadians) * normalizedChroma,
            Math.Sin(hueRadians) * normalizedChroma));
    }

    private static double Score(Bucket bucket, double totalWeight)
    {
        var color = bucket.Average;
        var population = Math.Pow(bucket.Weight / totalWeight, 0.42);
        var lightness = 1 - Math.Min(Math.Abs(color.Lightness - 0.61) / 0.48, 1);
        var chroma = color.Chroma;
        var chromaFitness = chroma < 0.08
            ? chroma / 0.08
            : 1 - (Math.Min(Math.Abs(chroma - 0.15), 0.2) / 0.4);

        return (population * 0.5) + (lightness * 0.18) + (chromaFitness * 0.32);
    }

    private sealed class Bucket
    {
        private double _lightness;
        private double _a;
        private double _b;

        public double Weight { get; private set; }

        public OklabColor Average => new(_lightness / Weight, _a / Weight, _b / Weight);

        public void Add(OklabColor color, double weight)
        {
            _lightness += color.Lightness * weight;
            _a += color.A * weight;
            _b += color.B * weight;
            Weight += weight;
        }
    }

    private readonly record struct Candidate(OklabColor Color, double Score);
}
