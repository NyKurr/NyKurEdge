using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Display;

public readonly record struct EdgeWindowBounds(int X, int Y, int Width, int Height);

public static class EdgeWindowLayout
{
    public const double CollapsedWidthDip = 112;
    public const double CollapsedHeightDip = 720;
    public const double ExpandedWidthDip = 432;
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
        var collapsedHeight = ToPixels(CollapsedHeightDip, scale, workArea.Height);
        var expandedWidth = ToPixels(ExpandedWidthDip, scale, workArea.Width);
        var expandedHeight = ToPixels(ExpandedHeightDip, scale, workArea.Height);

        var width = Interpolate(collapsedWidth, expandedWidth, progress);
        var height = Interpolate(collapsedHeight, expandedHeight, progress);
        var x = side == EdgeSide.Right
            ? workArea.X + workArea.Width - width
            : workArea.X;
        var y = workArea.Y + ((workArea.Height - height) / 2);

        return new EdgeWindowBounds(x, y, width, height);
    }

    private static int ToPixels(double dips, double scale, int availablePixels) =>
        Math.Clamp(
            (int)Math.Round(dips * scale, MidpointRounding.AwayFromZero),
            1,
            Math.Max(1, availablePixels));

    private static int Interpolate(int from, int to, double progress) =>
        (int)Math.Round(from + ((to - from) * progress));
}
