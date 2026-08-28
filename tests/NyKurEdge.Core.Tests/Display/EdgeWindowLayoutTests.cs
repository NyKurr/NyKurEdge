using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Tests.Display;

[TestClass]
public sealed class EdgeWindowLayoutTests
{
    private static readonly DisplayRect WorkArea = new(0, 0, 1920, 1040);

    [TestMethod]
    public void CollapsedRightEdgeIsCompactAndVerticallyCenteredAtOneHundredPercent()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 96, EdgeSide.Right, 0);

        Assert.AreEqual(112, bounds.Width);
        Assert.AreEqual(720, bounds.Height);
        Assert.AreEqual(1920 - 112, bounds.X);
        Assert.AreEqual(160, bounds.Y);
    }

    [TestMethod]
    public void CollapsedLeftEdgeMirrorsAtOneHundredTwentyFivePercent()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 120, EdgeSide.Left, 0);

        Assert.AreEqual(140, bounds.Width);
        Assert.AreEqual(900, bounds.Height);
        Assert.AreEqual(0, bounds.X);
        Assert.AreEqual(70, bounds.Y);
    }

    [TestMethod]
    public void ExpandedSurfaceGrowsInwardAndRemainsCentered()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 96, EdgeSide.Right, 1);

        Assert.AreEqual(432, bounds.Width);
        Assert.AreEqual(318, bounds.Height);
        Assert.AreEqual(1920 - 432, bounds.X);
        Assert.AreEqual(361, bounds.Y);
    }

    [TestMethod]
    public void ConstrainedWorkAreaClampsWidthAndPreservesOrigin()
    {
        var workArea = new DisplayRect(12, 20, 300, 220);

        var bounds = EdgeWindowLayout.Calculate(
            workArea,
            144,
            EdgeSide.Right,
            1);

        Assert.AreEqual(220, bounds.Height);
        Assert.AreEqual(20, bounds.Y);
        Assert.AreEqual(300, bounds.Width);
        Assert.AreEqual(12, bounds.X);
    }
}
