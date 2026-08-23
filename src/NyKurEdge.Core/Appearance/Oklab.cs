namespace NyKurEdge.Core.Appearance;

public static class Oklab
{
    public static OklabColor FromSrgb(byte red, byte green, byte blue)
    {
        var r = ToLinear(red / 255d);
        var g = ToLinear(green / 255d);
        var b = ToLinear(blue / 255d);

        var l = (0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b);
        var m = (0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b);
        var s = (0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b);

        var lRoot = Math.Cbrt(l);
        var mRoot = Math.Cbrt(m);
        var sRoot = Math.Cbrt(s);

        return new OklabColor(
            (0.2104542553 * lRoot) + (0.7936177850 * mRoot) - (0.0040720468 * sRoot),
            (1.9779984951 * lRoot) - (2.4285922050 * mRoot) + (0.4505937099 * sRoot),
            (0.0259040371 * lRoot) + (0.7827717662 * mRoot) - (0.8086757660 * sRoot));
    }

    public static AccentColor ToSrgb(OklabColor color)
    {
        var candidate = color;
        var hue = color.HueRadians;
        var chroma = color.Chroma;

        for (var attempt = 0; attempt < 18; attempt++)
        {
            var (r, g, b) = ToLinearRgb(candidate);
            if (IsInGamut(r, g, b))
            {
                return new AccentColor(ToByte(r), ToByte(g), ToByte(b));
            }

            chroma *= 0.9;
            candidate = new OklabColor(
                color.Lightness,
                Math.Cos(hue) * chroma,
                Math.Sin(hue) * chroma);
        }

        var neutral = ToLinearRgb(new OklabColor(color.Lightness, 0, 0));
        return new AccentColor(ToByte(neutral.Red), ToByte(neutral.Green), ToByte(neutral.Blue));
    }

    public static OklabColor Lerp(OklabColor from, OklabColor to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new OklabColor(
            from.Lightness + ((to.Lightness - from.Lightness) * amount),
            from.A + ((to.A - from.A) * amount),
            from.B + ((to.B - from.B) * amount));
    }

    private static (double Red, double Green, double Blue) ToLinearRgb(OklabColor color)
    {
        var lRoot = color.Lightness + (0.3963377774 * color.A) + (0.2158037573 * color.B);
        var mRoot = color.Lightness - (0.1055613458 * color.A) - (0.0638541728 * color.B);
        var sRoot = color.Lightness - (0.0894841775 * color.A) - (1.2914855480 * color.B);

        var l = lRoot * lRoot * lRoot;
        var m = mRoot * mRoot * mRoot;
        var s = sRoot * sRoot * sRoot;

        return (
            (+4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s),
            (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s),
            (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s));
    }

    private static bool IsInGamut(double red, double green, double blue) =>
        red is >= 0 and <= 1 && green is >= 0 and <= 1 && blue is >= 0 and <= 1;

    private static double ToLinear(double channel) =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static byte ToByte(double linear)
    {
        var srgb = linear <= 0.0031308
            ? 12.92 * linear
            : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
        return (byte)Math.Round(Math.Clamp(srgb, 0, 1) * 255);
    }
}
