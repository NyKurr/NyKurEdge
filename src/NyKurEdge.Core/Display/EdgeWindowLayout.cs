using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Display;

public readonly record struct EdgeWindowBounds(int X, int Y, int Width, int Height);

public static class EdgeWindowLayout
{
    // The host is transparent and click-through outside the launcher. The extra
    // horizontal allowance gives the broadest fluid stroke and glow a soft
    // falloff instead of flattening against the inner HWND boundary.
    public const double CollapsedWidthDip = 152;
    public const double ExpandedWidthDip = 432;
    // The visible expanded bloom remains compact even though the transparent
    // host window now spans the complete monitor work area.
    public const double ExpandedHeightDip = 318;

    public static EdgeWindowBounds Calculate(
        DisplayRect workArea,
        uint dpi,
        EdgeSide side,
        double expansionProgress)
    {
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var progress = Math.Clamp(expansionProgress, 0, 1);

        var collapsedWidth = ToPixels(CollapsedWidthDip, scale, workArea.Width);
        var expandedWidth = ToPixels(ExpandedWidthDip, scale, workArea.Width);

        var width = Interpolate(collapsedWidth, expandedWidth, progress);
        var x = side == EdgeSide.Right
            ? workArea.X + workArea.Width - width
            : workArea.X;

        return new EdgeWindowBounds(x, workArea.Y, width, workArea.Height);
    }

    private static int ToPixels(double dips, double scale, int availablePixels) =>
        Math.Clamp(
            (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero),
            1,
            Math.Max(1, availablePixels));

    private static int Interpolate(int from, int to, double progress) =>
        (int)Math.Round(from + ((to - from) * progress));
}
