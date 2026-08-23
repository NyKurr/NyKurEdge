using NyKurEdge.Core.Appearance;

namespace NyKurEdge.Core.Tests.Appearance;

[TestClass]
public sealed class AccentColorSelectorTests
{
    [TestMethod]
    public void NeonGreenIsCalmedWithoutLosingItsHue()
    {
        var pixels = Enumerable.Repeat(new Rgba32(0, 255, 0), 256).ToArray();

        var accent = AccentColorSelector.Select(pixels);

        Assert.IsGreaterThan(accent.Red, accent.Green);
        Assert.IsGreaterThan(accent.Blue, accent.Green);
        Assert.IsLessThan((byte)245, accent.Green, "The normalized accent should not remain neon green.");
        Assert.IsTrue(accent.Red > 0 || accent.Blue > 0, "Normalization should soften a fully saturated source.");
    }

    [TestMethod]
    public void NearBlackAndWhiteArtworkUsesTheFallback()
    {
        var pixels = Enumerable.Repeat(new Rgba32(2, 2, 2), 128)
            .Concat(Enumerable.Repeat(new Rgba32(252, 252, 252), 128))
            .ToArray();
        var fallback = new AccentColor(80, 100, 160);

        var accent = AccentColorSelector.Select(pixels, fallback);

        Assert.AreEqual(fallback, accent);
    }

    [TestMethod]
    public void ColoredSubjectWinsOverNeutralBackground()
    {
        var pixels = Enumerable.Repeat(new Rgba32(35, 36, 38), 800)
            .Concat(Enumerable.Repeat(new Rgba32(38, 92, 210), 200))
            .ToArray();

        var accent = AccentColorSelector.Select(pixels);

        Assert.IsGreaterThan(accent.Red, accent.Blue);
        Assert.IsGreaterThan(accent.Green, accent.Blue);
    }
}
