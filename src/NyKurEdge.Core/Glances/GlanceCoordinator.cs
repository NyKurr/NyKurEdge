using NyKurEdge.Core.Events;

namespace NyKurEdge.Core.Glances;

public sealed class GlanceCoordinator(IEventBus eventBus) : IGlanceCoordinator
{
    private readonly SemaphoreSlim _presentationGate = new(1, 1);
    private GlancePresentation? _current;

    public GlancePresentation? Current => Volatile.Read(ref _current);

    public async Task ShowAsync(GlancePresentation glance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(glance);
        await _presentationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _current, glance);
            await eventBus.PublishAsync(new GlanceRequested(glance), cancellationToken).ConfigureAwait(false);
            await Task.Delay(glance.Duration, cancellationToken).ConfigureAwait(false);
            await eventBus.PublishAsync(new GlanceEnded(glance.Id), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _current, null);
            _presentationGate.Release();
        }
    }

    public void Dispose()
    {
        _presentationGate.Dispose();
    }
}
