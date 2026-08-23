using NyKurEdge.Core.Events;

namespace NyKurEdge.Core.Settings;

public sealed class SettingsService(ISettingsStore store, IEventBus eventBus) : IDisposable
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private AppSettings _current = new();

    public AppSettings Current => Volatile.Read(ref _current);

    public bool IsInitialized { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized)
            {
                return;
            }

            _current = (await store.LoadAsync(cancellationToken).ConfigureAwait(false)).Normalize();
            IsInitialized = true;
        }
        finally
        {
            _mutex.Release();
        }

        await eventBus.PublishAsync(new SettingsChanged(Current), cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        AppSettings updated;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updated = update(_current).Normalize();
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, updated);
        }
        finally
        {
            _mutex.Release();
        }

        await eventBus.PublishAsync(new SettingsChanged(updated), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _mutex.Dispose();
    }
}
