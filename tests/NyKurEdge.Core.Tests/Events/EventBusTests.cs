using NyKurEdge.Core.Events;

namespace NyKurEdge.Core.Tests.Events;

[TestClass]
public sealed class EventBusTests
{
    [TestMethod]
    public async Task DisposedSubscriptionStopsReceivingEvents()
    {
        var eventBus = new EventBus();
        var received = 0;
        var subscription = eventBus.Subscribe<string>((_, _) =>
        {
            received++;
            return ValueTask.CompletedTask;
        });

        await eventBus.PublishAsync("first");
        subscription.Dispose();
        await eventBus.PublishAsync("second");

        Assert.AreEqual(1, received);
    }
}
