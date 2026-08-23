using System.Globalization;

namespace NyKurEdge.Core.Appearance;

public readonly record struct AccentColor(byte Red, byte Green, byte Blue)
{
    public static AccentColor Default { get; } = new(114, 134, 232);

    public string ToHex() => $"#{Red:X2}{Green:X2}{Blue:X2}";

    public static bool TryParse(string? value, out AccentColor color)
    {
        color = Default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new AccentColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}

public readonly record struct Rgba32(byte Red, byte Green, byte Blue, byte Alpha = 255);

public readonly record struct OklabColor(double Lightness, double A, double B)
{
    public double Chroma => Math.Sqrt((A * A) + (B * B));

    public double HueRadians => Math.Atan2(B, A);
}
