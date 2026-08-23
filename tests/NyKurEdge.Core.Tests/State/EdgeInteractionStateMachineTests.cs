using NyKurEdge.Core.State;

namespace NyKurEdge.Core.Tests.State;

[TestClass]
public sealed class EdgeInteractionStateMachineTests
{
    [TestMethod]
    public void ReenteringDuringGracePeriodPreventsCollapse()
    {
        var machine = new EdgeInteractionStateMachine(TimeSpan.FromMilliseconds(400));
        var now = DateTimeOffset.UtcNow;

        machine.PointerEntered();
        machine.PointerExited(now);
        machine.PointerEntered();
        var state = machine.Advance(now.AddSeconds(1));

        Assert.AreEqual(EdgeVisibility.Expanded, state.Visibility);
        Assert.IsTrue(state.IsPointerInside);
    }

    [TestMethod]
    public void GlanceKeepsEdgeOpenUntilItsOwnGracePeriodEnds()
    {
        var machine = new EdgeInteractionStateMachine(TimeSpan.FromMilliseconds(300));
        var now = DateTimeOffset.UtcNow;

        machine.BeginGlance();
        machine.PointerExited(now);
        Assert.AreEqual(EdgeVisibility.Expanded, machine.Advance(now.AddSeconds(2)).Visibility);

        machine.EndGlance(now.AddSeconds(2));
        Assert.AreEqual(EdgeVisibility.Collapsed, machine.Advance(now.AddSeconds(3)).Visibility);
    }
}
