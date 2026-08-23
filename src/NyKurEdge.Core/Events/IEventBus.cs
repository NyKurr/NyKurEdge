namespace NyKurEdge.Core.Events;

public interface IEventBus
{
    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler);

    ValueTask PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default);
}
