using NyKurEdge.Core.Display;
using NyKurEdge.Core.Settings;

namespace NyKurEdge.Core.Tests.Display;

[TestClass]
public sealed class EdgeWindowLayoutTests
{
    private static readonly DisplayRect WorkArea = new(0, 0, 1920, 1040);

    [TestMethod]
    public void CollapsedRightEdgeIsLocalizedAndCenteredAtOneHundredPercent()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 96, EdgeSide.Right, 0);

        Assert.AreEqual(82, bounds.Width);
        Assert.AreEqual(320, bounds.Height);
        Assert.AreEqual(1920 - 82, bounds.X);
        Assert.AreEqual((1040 - 320) / 2, bounds.Y);
    }

    [TestMethod]
    public void CollapsedLeftEdgeMirrorsAtOneHundredTwentyFivePercent()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 120, EdgeSide.Left, 0);

        Assert.AreEqual(103, bounds.Width);
        Assert.AreEqual(400, bounds.Height);
        Assert.AreEqual(0, bounds.X);
        Assert.AreEqual((1040 - 400) / 2, bounds.Y);
    }

    [TestMethod]
    public void ExpandedSurfaceRemainsCenteredAndBelowWorkAreaHeight()
    {
        var bounds = EdgeWindowLayout.Calculate(WorkArea, 96, EdgeSide.Right, 1);

        Assert.AreEqual(388, bounds.Width);
        Assert.AreEqual(560, bounds.Height);
        Assert.AreEqual(1920 - 388, bounds.X);
        Assert.AreEqual((1040 - 560) / 2, bounds.Y);
    }

    [TestMethod]
    public void CenterOffsetIsClampedInsideAConstrainedWorkArea()
    {
        var workArea = new DisplayRect(12, 20, 300, 220);

        var bounds = EdgeWindowLayout.Calculate(
            workArea,
            144,
            EdgeSide.Right,
            1,
            centerOffsetDip: 500);

        Assert.AreEqual(220, bounds.Height);
        Assert.AreEqual(20, bounds.Y);
        Assert.AreEqual(300, bounds.Width);
        Assert.AreEqual(12, bounds.X);
    }
}
