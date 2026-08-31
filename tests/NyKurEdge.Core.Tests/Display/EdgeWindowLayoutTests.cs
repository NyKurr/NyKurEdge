using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Tests.Display;

[TestClass]
public sealed class EdgeWindowLayoutTests
{
    private static readonly DisplayRect WorkArea = new(0, 0, 1920, 1040);

    [TestMethod]
    public void CollapsedRightEdgeSpansWorkAreaHeightAtOneHundredPercent()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 96, EdgeSide.Right, 0);

        Assert.AreEqual(152, bounds.Width);
        Assert.AreEqual(1040, bounds.Height);
        Assert.AreEqual(1920 - 152, bounds.X);
        Assert.AreEqual(0, bounds.Y);
    }

    [TestMethod]
    public void CollapsedLeftEdgeSpansWorkAreaHeightAtOneHundredTwentyFivePercent()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 120, EdgeSide.Left, 0);

        Assert.AreEqual(190, bounds.Width);
        Assert.AreEqual(1040, bounds.Height);
        Assert.AreEqual(0, bounds.X);
        Assert.AreEqual(0, bounds.Y);
    }

    [TestMethod]
    public void ExpandedSurfaceGrowsInwardWithoutChangingVerticalBounds()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 96, EdgeSide.Right, 1);

        Assert.AreEqual(432, bounds.Width);
        Assert.AreEqual(1040, bounds.Height);
        Assert.AreEqual(1920 - 432, bounds.X);
        Assert.AreEqual(0, bounds.Y);
    }

    [TestMethod]
    public void IntermediateExpansionOnlyInterpolatesWidth()
    {
        var workArea = new DisplayRect(40, 24, 1600, 900);

        var bounds = EdgeWindowLayout.Calculate(workArea, 96, EdgeSide.Left, 0.5);

        Assert.AreEqual(292, bounds.Width);
        Assert.AreEqual(900, bounds.Height);
        Assert.AreEqual(40, bounds.X);
        Assert.AreEqual(24, bounds.Y);
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
