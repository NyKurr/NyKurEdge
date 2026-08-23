namespace NyKurEdge.Core.Events;

public sealed class EventBus : IEventBus
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Type, List<ISubscription>> _subscriptions = [];

    public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription<TEvent>(this, handler);
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var handlers))
            {
                handlers = [];
                _subscriptions.Add(typeof(TEvent), handlers);
            }

            handlers.Add(subscription);
        }

        return subscription;
    }

    public async ValueTask PublishAsync<TEvent>(TEvent message, CancellationToken cancellationToken = default)
    {
        ISubscription[] handlers;
        lock (_gate)
        {
            handlers = _subscriptions.TryGetValue(typeof(TEvent), out var registered)
                ? [.. registered]
                : [];
        }

        foreach (var handler in handlers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler.InvokeAsync(message!, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Remove(ISubscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscription.MessageType, out var handlers))
            {
                return;
            }

            handlers.Remove(subscription);
            if (handlers.Count == 0)
            {
                _subscriptions.Remove(subscription.MessageType);
            }
        }
    }

    private interface ISubscription
    {
        Type MessageType { get; }

        ValueTask InvokeAsync(object message, CancellationToken cancellationToken);
    }

    private sealed class Subscription<TEvent>(
        EventBus owner,
        Func<TEvent, CancellationToken, ValueTask> handler) : ISubscription, IDisposable
    {
        private EventBus? _owner = owner;

        public Type MessageType => typeof(TEvent);

        public ValueTask InvokeAsync(object message, CancellationToken cancellationToken) =>
            handler((TEvent)message, cancellationToken);

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Remove(this);
        }
    }
}
